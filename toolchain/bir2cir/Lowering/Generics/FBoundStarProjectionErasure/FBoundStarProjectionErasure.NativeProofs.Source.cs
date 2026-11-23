using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using DeclarationBinding = CurrentDeclarationBindings.DeclarationBinding;

static partial class FBoundStarProjectionErasure
{
    internal static NativeProofPlan PrepareNativeProofs(CurrentDeclarationBindings declarations,
        ReferenceMetadataIndex refs, GenericRepresentationPolicy values) => new(declarations, refs, values);

    internal sealed partial class NativeProofPlan
    {
        internal sealed record ConstructorEdge(string Id, TypeNode.Fqn Application);
        readonly Dictionary<DeclarationBinding, List<ConstructorEdge>> forwardedConstructors = new();
        sealed record CallableEdge(string Caller, string Target, TypeNode[] Arguments, TypeNode[] Owner, bool Constructor);
        readonly List<CallableEdge> callableEdges = [];
        readonly Dictionary<string, TypeNode[][]> callableBounds = new(StringComparer.Ordinal);

        void CaptureCallableEdge(DeclarationBinding caller, string target, TypeNode[] args, TypeNode[] owner, bool constructor)
        {
            if (caller?.Source is not BirFunctionDeclaration { DeclarationId.HasValue: true } method || target == null
                || !args.Any(a => a is TypeNode.Tv { Scope: "method" })) return;
            var id = method.DeclarationId.Value;
            if (!callableBounds.ContainsKey(id))
                callableBounds[id] = Constraints(caller).Select(row => row.Select(BirGraphValue.CopyType).ToArray()).ToArray();
            callableEdges.Add(new(id, target, args.ToArray(), owner?.ToArray() ?? [], constructor));
        }

        // Bound propagation is finite: admit only a facet already declared by the caller.
        // The selected construction/call, not the mere existence of a source bound, owns this obligation.
        internal Dictionary<string, HashSet<(int Primary, TypeNode Bound)>> CompleteCallableFacets(SourceCarrierDeclarations declarations)
        {
            var result = new Dictionary<string, HashSet<(int, TypeNode)>>(StringComparer.Ordinal);
            var uses = callableEdges.GroupBy(e => e.Target).ToDictionary(g => g.Key, g => g.ToArray());
            var pending = new Queue<(CallableEdge Call, int Slot, TypeNode Bound)>();
            foreach (var edge in callableEdges)
            {
                if (edge.Constructor)
                {
                    var id = StorageFactoryId(edge.Target);
                    var plan = declarations.ProjectedConstructors.TryGetValue(id, out var local)
                        ? local.Plan : references.ConstructorPlan(id, nativeNames: true);
                    if (plan != null)
                        foreach (var f in plan.Facets.Where(f => f.NativeArgument)) pending.Enqueue((edge, f.Primary, f.Bound));
                }
                else if (!methods.ContainsKey(edge.Target) && references.ProjectedMethod(ProjectedCallableEntries.EntryId(edge.Target)) is { } imported)
                    foreach (var f in imported.Facets.Where(f => f.NativeArgument)) pending.Enqueue((edge, f.OwnerIndex, f.Bound));
            }
            while (pending.TryDequeue(out var next))
            {
                var (edge, slot, bound) = next;
                if (slot >= edge.Arguments.Length || edge.Arguments[slot] is not TypeNode.Tv { Scope: "method" } input) continue;
                var required = SubstituteDeclarationTypeArguments(bound,
                    edge.Constructor ? edge.Arguments : edge.Owner, edge.Constructor ? [] : edge.Arguments);
                var key = references.WitnessConstraintKey(required);
                var caller = edge.Caller;
                var declared = callableBounds[caller][input.I];
                var matching = declared.FirstOrDefault(b => references.WitnessConstraintKey(b) == key)
                    ?? declared.FirstOrDefault(b => ProjectedConstructorPlan.Proves(b, required, sourceSupertypes, references));
                if (matching == null) continue;
                if (!result.TryGetValue(caller, out var facets)) result[caller] = facets = [];
                if (facets.Add((input.I, references.WitnessConstraintKey(matching))) && uses.TryGetValue(caller, out var callers))
                    foreach (var use in callers.Where(e => !e.Constructor)) pending.Enqueue((use, input.I, matching));
            }
            return result;
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
                    if (captureEdges && node is BirNew allocation)
                        CaptureCallableEdge(method, allocation.ConstructorDeclarationId.OrDefault(), constructorArguments, null, true);
                    if (captureEdges && node is BirNew creation && creation.ConstructorDeclarationId.OrDefault() is string id &&
                        constructorArguments.Any(a => a is TypeNode.Tv { Scope: "type" }))
                    {
                        if (!forwardedConstructors.TryGetValue(owner, out var uses)) forwardedConstructors[owner] = uses = [];
                        uses.Add(new(id, construction));
                    }
                }
                if (node.ApplicationTypeArguments.HasValue)
                {
                    var methodArguments = node.ApplicationTypeArguments.Value;
                    var selectedOwner = SelectedOwner(node);
                    var identity = node.DeclarationIdentityFact.OrDefault();
                    if (captureEdges) CaptureCallableEdge(method, identity, methodArguments, selectedOwner?.Args, false);
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
                        parameterOwnerArguments: selectedOwner.Args ?? Array.Empty<TypeNode>());
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
