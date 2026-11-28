using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using System.Reflection;
using DeclarationBinding = CurrentDeclarationBindings.DeclarationBinding;

static partial class FBoundStarProjectionErasure
{
    internal static NativeProofPlan PrepareNativeProofs(CurrentDeclarationBindings declarations,
        ReferenceMetadataIndex refs, GenericRepresentationPolicy values) => new(declarations, refs, values);

    internal sealed partial class NativeProofPlan
    {
        internal sealed record ConstructorEdge(string Id, TypeNode.Fqn Application);
        readonly Dictionary<DeclarationBinding, List<ConstructorEdge>> forwardedConstructors = new();
        readonly Dictionary<BirFunctionDeclaration, (int Arity, HashSet<int> Positions)> refLikeOverrideRows = new(ReferenceEqualityComparer.Instance);

        // Admission of a stack-only argument is a property of the selected CLR
        // slot, not a Kotlin type bound. Do not change the signature used to
        // select Kotlin overrides or copied to their covariant bridges.
        internal void ApplyRefLikeOverrideRows()
        {
            foreach (var (method, admission) in refLikeOverrideRows)
            {
                var parameters = method.TypeParams.Value;
                if (parameters.Length != admission.Arity)
                    throw new InvalidOperationException($"Native override frame changed for '{method.Name.Value}'.");
                foreach (var position in admission.Positions)
                {
                    var row = parameters[position];
                    if (!row.SpecialConstraints.OrEmpty().Contains("allowsRefStruct"))
                        row.SpecialConstraints = row.SpecialConstraints.OrEmpty().Append("allowsRefStruct").ToArray();
                }
            }
        }
        internal IEnumerable<ConstructorEdge> ForwardedConstructors(DeclarationBinding owner) =>
            owner != null && forwardedConstructors.TryGetValue(owner, out var uses) ? uses : [];

        internal NativeProofPlan(CurrentDeclarationBindings declarations, ReferenceMetadataIndex references,
            GenericRepresentationPolicy values)
        {
            bindings = declarations;
            this.references = references;
            definitions = declarations.SourceDefinitions;
            sourceSupertypes = declarations.SourceSupertypes;
            methods = declarations.SourceLocalDeclarations.Values.GroupBy(Identity)
                .Where(group => group.Key != null && group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            foreach (var declaration in definitions.Values.Concat(declarations.SourceRoots))
            {
                Register(declaration, !Generated(declaration));
                var fields = declaration.Source switch {
                    IBirTypeDeclaration type => type.Fields.OrEmpty(),
                    BirFileDeclaration file => file.Fields.OrEmpty(), _ => Array.Empty<BirFieldDeclaration>(),
                };
                foreach (var field in fields.Where(field => values.IsNativeField(field)))
                    if (field.Type.HasValue)
                        BirGraphNameFields.VisitTypeNodes(field.Type.Value, type => {
                            if (type is TypeNode.Tv { Scope: "type" } variable)
                                rows[declaration].TryAdd(variable.I, new List<TypeNode>());
                        });
                var declaredMethods = declaration.Source switch {
                    IBirTypeDeclaration type => type.Methods.OrEmpty(),
                    BirFileDeclaration file => file.Methods.OrEmpty(), _ => Array.Empty<BirFunctionDeclaration>(),
                };
                foreach (var method in declaredMethods)
                {
                    var binding = declarations.GetSource(method);
                    Register(binding, false);
                    RegisterNativeOverrides(method, binding);
                }
            }
            // A fake override has no MethodDef on which to widen admission. Check
            // the inherited declaration after collecting every local override row.
            foreach (var definition in definitions.Values)
                if (definition.Source is BirClassDefinition owner)
                    foreach (var fact in owner.InheritedClassMethods.OrEmpty())
                        CheckInheritedAdmission(owner, fact);
            bool changed, captureEdges = true;
            do
            {
                changed = false;
                foreach (var root in declarations.SourceRoots) Walk(root.Source, root, null);
                captureEdges = false;
            } while (changed);

            void Walk(BirGraphValue node, DeclarationBinding owner, DeclarationBinding method)
            {
                if (node is IBirExecutablePayload) return;
                if (node is IBirTypeDeclaration type && IsTypeDefinition(type.Kind.OrDefault()))
                {
                    owner = declarations.GetSource(node);
                    method = null;
                }
                else if (OwnCallableContext(node)) method = declarations.GetSource(node);
                var construction = node switch {
                    BirNew value => value.Type.OrDefault() as TypeNode.Fqn,
                    BirNewClr value => value.Type.OrDefault() as TypeNode.Fqn,
                    BirNewClosure value => value.ClosureType.OrDefault() as TypeNode.Fqn,
                    BirNewSam value => value.SamType.OrDefault() as TypeNode.Fqn, _ => null,
                };
                if (construction?.Args is { } constructorArguments)
                {
                    Require(TypeRows(construction.Name), constructorArguments, "type", null);
                    if (captureEdges && constructorArguments.Any(a => a is TypeNode.Tv { Scope: "type" }))
                    {
                        var id = (node as BirNew)?.ConstructorDeclarationId.OrDefault();
                        if (id != null || node is BirNewClr)
                        {
                            if (!forwardedConstructors.TryGetValue(owner, out var uses)) forwardedConstructors[owner] = uses = [];
                            uses.Add(new(id, construction));
                        }
                    }
                }
                if (node.ApplicationTypeArguments.HasValue)
                {
                    var methodArguments = node.ApplicationTypeArguments.Value;
                    var selectedOwner = SelectedOwner(node);
                    var identity = node.DeclarationIdentityFact.OrDefault();
                    var selected = identity != null && selectedOwner != null
                        && declarations.SourceLocalDeclarations.TryGetValue((selectedOwner.Name, identity), out var local)
                            ? local : methods.GetValueOrDefault(identity ?? "");
                    var required = node switch {
                        BirGenericStatic call => ReadRows(ClrMemberResolution.SelectedMethodTypeParametersInPhysicalFrame(null, call, references),
                            false, physicalDeclaration: true),
                        BirGenericInstance call => ReadRows(ClrMemberResolution.SelectedMethodTypeParametersInPhysicalFrame(null, call, references),
                            false, physicalDeclaration: true),
                        _ => selected != null ? Rows(selected) : MethodRows(identity),
                    };
                    Require(required, methodArguments, "method", selectedOwner?.Args);
                }
                node.VisitOwnChildSlots((field, role, slot) => {
                    if (field is not ("attrs" or "typeParams" or "memberRef") && !NullableRepresentationViews.Frozen(role))
                        Walk(slot.Value, owner, method);
                });

                void Require(IReadOnlyDictionary<int, List<TypeNode>> required, TypeNode[] arguments,
                    string scope, TypeNode[] ownerArguments)
                {
                    foreach (var (index, constraints) in required.ToArray())
                    {
                        if (index >= arguments.Length || arguments[index] is not TypeNode.Tv actual) continue;
                        var context = actual.Scope == "method" ? method : owner;
                        if (context == null) continue;
                        foreach (var constraint in constraints.ToArray())
                        {
                            var closed = scope == "type" ? SupertypeGraph.SubstOwnerTvs(constraint, arguments)
                                : SubstituteMethodTypeArguments(constraint, arguments);
                            if (scope == "method" && ownerArguments != null)
                                closed = SupertypeGraph.SubstOwnerTvs(closed, ownerArguments);
                            changed |= Add(context, actual.I, closed);
                        }
                    }
                }
            }

            void CheckInheritedAdmission(BirClassDefinition owner, BirInheritedClassMethod fact)
            {
                if (fact.InheritedImplementation.OrDefault() is not { } parent
                    || parent.Owner.OrDefault() is not TypeNode.Fqn baseType
                    || parent.TypeParams.OrEmpty().Length == 0) return;
                var id = parent.DeclarationId.OrDefault();
                var kotlinParent = values.OwnsValueSlots(baseType.Name);
                if (kotlinParent && id == null || values.IsBindingAlias(baseType.Name)) return;
                System.Type[] nativeRows = null;
                var parentKey = parent.NativeMethodKey.OrDefault();
                foreach (var edge in fact.Overrides.OrEmpty())
                {
                    var key = edge.NativeMethodKey.OrDefault();
                    if ((key == null && (kotlinParent || parentKey == null)) ||
                        edge.Owner.OrDefault() is not TypeNode.Fqn slot || values.IsBindingAlias(slot.Name) ||
                        values.OwnsValueSlots(slot.Name) || !references.IsInterfaceType(slot)) continue;
                    var required = key == null ? null : references.SelectProjectedNativeBase(
                        ClrMemberResolution.ResolveOwnerType(slot, references), edge.Member.Value, key).GetGenericArguments();
                    if (!kotlinParent && parentKey != null)
                        nativeRows ??= ClrDeclarationMatching.MethodForNativeKey(references,
                            ClrMemberResolution.ResolveOwnerType(baseType, references), parent.Member.Value,
                            parent.Arity.Value, true, parentKey).GetGenericArguments();
                    for (int i = 0; i < (required?.Length ?? nativeRows.Length); i++)
                    {
                        var slotFlags = required?[i].GenericParameterAttributes ?? 0;
                        var baseFlags = nativeRows?[i].GenericParameterAttributes ?? 0;
                        if (AdditionalConstraint(baseFlags, slotFlags) is string extra)
                            throw new InvalidOperationException($"'{owner.Name.Value}' must explicitly implement '{slot.Name}.{edge.Member.Value}': " +
                                $"inherited '{baseType.Name}.{parent.Member.Value}' additionally requires '{extra}' for type parameter {i + 1}.");
                        if ((slotFlags & GenericParameterAttributes.AllowByRefLike) == 0) continue;
                        bool admits = !kotlinParent ? (baseFlags & GenericParameterAttributes.AllowByRefLike) != 0
                            : declarations.SourceLocalDeclarations.TryGetValue((baseType.Name, id), out var local)
                            && local.Source is BirFunctionDeclaration method
                            ? LocalAdmits(method, i)
                            : references.InheritedParameterAdmitsRefLike(id, i);
                        if (!admits)
                            throw new InvalidOperationException(
                                $"'{owner.Name.Value}' must explicitly implement '{slot.Name}.{edge.Member.Value}': " +
                                $"inherited '{baseType.Name}.{parent.Member.Value}' does not admit stack-only types for type parameter {i + 1}.");
                    }
                }
                static string AdditionalConstraint(GenericParameterAttributes body, GenericParameterAttributes slot)
                {
                    // Unlike allows-ref-struct, these flags *restrict* the method's domain.
                    // A non-nullable value type also guarantees a default constructor.
                    if ((slot & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
                        slot |= GenericParameterAttributes.DefaultConstructorConstraint;
                    var extra = body & ~slot;
                    if ((extra & GenericParameterAttributes.ReferenceTypeConstraint) != 0) return "class";
                    if ((extra & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0) return "struct";
                    return (extra & GenericParameterAttributes.DefaultConstructorConstraint) != 0 ? "new()" : null;
                }
                bool LocalAdmits(BirFunctionDeclaration method, int sourceIndex)
                {
                    int position = method.NullableRepresentationFrame.OrDefault()?.SourcePosition(sourceIndex) ?? sourceIndex;
                    return method.TypeParams.Value[position].SpecialConstraints.OrEmpty().Contains("allowsRefStruct")
                        || refLikeOverrideRows.TryGetValue(method, out var admission) && admission.Positions.Contains(position);
                }
            }

            void RegisterNativeOverrides(BirFunctionDeclaration method, DeclarationBinding binding)
            {
                var parameters = method.TypeParams.OrEmpty();
                if (parameters.Length == 0) return;
                foreach (var edge in method.Overrides.OrEmpty())
                {
                    if (edge.Owner.OrDefault() is not TypeNode.Fqn selectedOwner
                        || values.IsBindingAlias(selectedOwner.Name) || values.OwnsValueSlots(selectedOwner.Name)) continue;
                    var selected = ClrMemberResolution.SelectedMethodTypeParametersInPhysicalFrame(null, selectedOwner,
                        edge.Member.OrDefault(), parameters.Length, true,
                        method.Params.OrEmpty().Select(parameter => parameter.Type.OrDefault()).ToArray(), references,
                        parameterOwnerArguments: selectedOwner.Args ?? Array.Empty<TypeNode>(), nativeMethodKey: edge.NativeMethodKey.OrDefault());
                    for (int i = 0; i < selected.Length; i++)
                        if (selected[i].SpecialConstraints.OrEmpty().Contains("allowsRefStruct"))
                        {
                            if (!refLikeOverrideRows.TryGetValue(method, out var admission))
                                refLikeOverrideRows[method] = admission = (selected.Length, new());
                            admission.Positions.Add(i);
                        }
                    foreach (var (index, bounds) in ReadRows(selected, false, physicalDeclaration: true))
                        foreach (var bound in bounds)
                            Add(binding, index, SupertypeGraph.SubstOwnerTvs(bound, selectedOwner.Args ?? Array.Empty<TypeNode>()));
                }
            }
        }

        static bool OwnCallableContext(BirGraphValue node) => node switch {
            BirFunctionDeclaration value => value.Params.HasValue && value.Body.HasValue,
            BirConstructor value => value.Params.HasValue && value.Body.HasValue,
            BirClosureDefinition value => value.Params.HasValue && value.Body.HasValue,
            BirNewSuspendLambda value => value.Params.HasValue && value.Body.HasValue,
            BirInlineLambda value => value.Params.HasValue && value.Body.HasValue, _ => false,
        };

        static TypeNode.Fqn SelectedOwner(BirGraphValue node)
        {
            TypeNode callee = null, ownerType = null, owner = null, type = null;
            node.VisitOwnTypeFields((field, value) => {
                switch (field) {
                    case "calleeOwner": callee = value; break;
                    case "ownerType": ownerType = value; break;
                    case "owner": owner = value; break;
                    case "type": type = value; break;
                }
            });
            return (callee ?? ownerType ?? owner ?? type) as TypeNode.Fqn;
        }
    }
}
