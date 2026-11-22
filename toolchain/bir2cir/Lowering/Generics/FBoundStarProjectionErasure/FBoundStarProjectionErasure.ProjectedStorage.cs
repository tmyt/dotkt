using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal sealed record ProjectedStorageOwner(string Exact, string Carrier, string Implementation, string Outer, string OuterCarrier,
        ProjectedClassStorageFrame.InterfaceSlot[] Slots, IReadOnlyDictionary<string, string> Constructors);

    static string StorageFactoryId(string constructorId) =>
        DeclarationIdentityBinding.PhysicalOnlyId(constructorId, DeclarationIdentityBinding.ProjectedConstructorRole);

    // The physical entry lives on a sibling carrier, so a source-private
    // constructor needs assembly access, not a new public construction path.
    // CallableVisibilityLowering has already promoted @PublishedApi entries.
    static string FactoryVisibility(BirConstructor constructor) => constructor.Vis.OrDefault("public") switch {
        "private" => "internal", "protected" => "protectedInternal", var visibility => visibility,
    };

    static void RegisterProjectedStorage(Owner owner, IBirTypeDeclaration definition, BirInterfaceDefinition carrier,
        CarrierRealizationContext context)
    {
        if (definition is not BirClassDefinition concrete || concrete.Abstract.OrDefault() || definition.Kind.OrDefault() != "class"
            || context.Projection.StorageFrame(owner.Name) is not { } frame) return;
        var constructors = definition.Ctors.OrEmpty().Where(c => c.DeclarationId.HasValue).ToArray();
        if (constructors.Length == 0) return;
        var used = carrier.Methods.OrEmpty().Select(method => method.Name.OrDefault()).ToHashSet(StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var entry = frame.Constructor(owner.OuterCount);
        int next = 0;
        foreach (var constructor in constructors)
        {
            var id = constructor.DeclarationId.Value;
            var factoryId = StorageFactoryId(id);
            string name;
            do name = "$projectedCtor$" + next++; while (!used.Add(name));
            ids.Add(id, factoryId);
            context.Declarations.ProjectedConstructors.Add(factoryId, (owner.ErasedName, name, entry));
            var generic = frame.Declare();
            var methodFrame = Enumerable.Range(0, generic.Length).Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray();
            TypeNode InMethod(TypeNode type) => SupertypeGraph.SubstOwnerTvs(type, methodFrame);
            foreach (var parameter in generic) parameter.RewriteTypeFieldsInPlace(InMethod);
            var parameters = constructor.Params.OrEmpty().Select(parameter => (BirParameter)parameter.DeepClone()).ToArray();
            foreach (var parameter in parameters) parameter.RewriteTypeFieldsInPlace(InMethod);
            var factory = new BirFunctionDeclaration {
                Name = name, DeclarationSourceName = name, DeclarationId = factoryId,
                Static = true, Virtual = false, Override = false, Abstract = false, Vis = FactoryVisibility(constructor), Generated = true,
                TypeParams = generic, Params = parameters, Ret = new TypeNode.Fqn(owner.ErasedName),
                // The implementation's constructor frame exists only after physical lowering.
                Body = Array.Empty<IBirStatement>(),
            };
            carrier.Methods = carrier.Methods.OrEmpty().Append(factory).ToArray();
        }
        var outer = owner.Inner && owner.SemanticOwner != null && context.Owners.TryGetValue(owner.SemanticOwner, out var enclosing)
            && enclosing.Needed ? enclosing : null;
        context.ProjectedStorage.Add(new(owner.Name, owner.ErasedName,
            context.Demand.AllocateReferenceContractName(owner.Name + "$projected"), outer?.Name, outer?.ErasedName, frame.Interfaces, ids));
    }

    internal static void MaterializeProjectedStorage(BirFileDeclaration[] documents, CarrierRealizationContinuation carriers,
        HashSet<string> emittedTypes, ReferenceMetadataIndex references, bool referenceBuild)
    {
        var callables = new ProjectedCallableEntries(documents, references, referenceBuild);
        if (carriers.ProjectedStorage.Count == 0) { callables.Finish(documents, emittedTypes); return; }
        var declarations = documents.SelectMany(file => file.Types.OrEmpty().Select(type => (file, type)))
            .ToLookup(row => row.type.Name.OrDefault(), StringComparer.Ordinal);
        // Slot positions survived lowering; a bound's spelling need not
        // (nested owner placement, aliases, nullable representations).
        var storageOwners = carriers.ProjectedStorage.ToDictionary(p => p.Exact, p => {
            var parameters = TypeParameterFrame.Declarations(declarations[p.Exact].Single().type).ToArray();
            return p with { Slots = p.Slots.Select(slot => slot with {
                Bound = parameters[slot.OwnerIndex].Constraints.Value[slot.BoundIndex] }).ToArray() };
        }, StringComparer.Ordinal);
        var entries = storageOwners.Values.ToDictionary(p => p.Exact, p => {
            var type = declarations[p.Exact].Single().type;
            return ProjectedConstructorPlan.From(TypeParameterFrame.Declarations(type).Count(),
                type.CapturedTypeParams.OrEmpty().Length, p.Slots);
        }, StringComparer.Ordinal);
        var implementations = new Dictionary<string, BirClassDefinition>(StringComparer.Ordinal);
        var innerFactories = new List<(ProjectedStorageOwner Plan, string Id, BirFunctionDeclaration Factory, int Prefix, int Original)>();
        foreach (var plan in storageOwners.Values)
        {
            var (file, type) = declarations[plan.Exact].Single();
            var carrier = declarations[plan.Carrier].Single().type;
            var exact = (BirClassDefinition)type;
            var storage = (BirClassDefinition)exact.DeepClone();
            implementations.Add(plan.Exact, storage);
            storage.Name = plan.Implementation;
            storage.Generated = true;
            storage.Vis = "internal";
            storage.SemanticOwner = default;
            storage.RoundtripTypeHeaderPending = default;
            storage.RoundtripSupertypesPre = default;
            // The plan uses CLR slot indices. Placement has since separated
            // an inner class's enclosing prefix from its own type parameters.
            var fullFrame = ProjectedClassStorageFrame.Declare(TypeParameterFrame.Declarations(exact).ToArray(), plan.Slots);
            var prefix = exact.CapturedTypeParams.OrEmpty().Length;
            if (prefix != 0) storage.CapturedTypeParams = fullFrame[..prefix];
            storage.TypeParams = fullFrame[prefix..];
            // An exact interface application can require both halves on a
            // single CLR parameter. The split implementation instead implements
            // its Kotlin interface through its declaration's existing carrier.
            TypeNode ProjectInterface(TypeNode type)
            {
                if (type is not TypeNode.Fqn { Args: { } args } face ||
                    !carriers.LocalOwners.TryGetValue(face.Name, out var erased) &&
                    !references.TryExistentialPhysicalOwner(face.Name, out erased)) return type;
                var formal = declarations[face.Name].FirstOrDefault().type is { } local
                    ? TypeParameterFrame.Declarations(local).ToArray()
                    : references.PhysicalOwnerTypeParametersInPhysicalFrame(face.Name);
                if (formal == null || formal.Length != args.Length) return type;
                for (var i = 0; i < args.Length; i++)
                    if (args[i] is TypeNode.Tv { Scope: "type" } actual &&
                        formal[i].Constraints.OrEmpty().Any(constraint => plan.Slots.Any(s => s.OwnerIndex == actual.I &&
                            s.Bound.Equals(SupertypeGraph.SubstOwnerTvs(constraint, args)))))
                        return new TypeNode.Fqn(erased);
                return type;
            }
            storage.Interfaces = storage.Interfaces.OrEmpty().Select(ProjectInterface).DistinctBy(SupertypeGraph.TypeKey).ToArray();
            ProjectNativeInterfaceStorage(storage, plan, references);
            storage.Attrs = storage.Attrs.OrEmpty().Where(a =>
                a.Attr.OrDefault() is TypeNode.Fqn { Name: RoundtripMetadata.RuntimeClassifierAttribute }).ToArray();
            storage.Methods = storage.Methods.OrEmpty().Where(m => !m.Static.OrDefault()).ToArray();
            storage.Fields = storage.Fields.OrEmpty().Where(f => !f.Static.OrDefault()).ToArray();
            var count = fullFrame.Length;
            var exactParameters = TypeParameterFrame.Declarations(exact).ToArray();
            TypeNode.Fqn Instance(string scope) => new(plan.Implementation,
                Enumerable.Range(0, count).Select(i => (TypeNode)new TypeNode.Tv(scope, i)).ToArray());
            if (!referenceBuild)
            {
                callables.BindExactOwnerMethods(exact, exactParameters);
                var generated = new List<IBirTypeDeclaration>();
                var ordinal = 0;
                string Allocate(string role)
                {
                    string name;
                    do name = plan.Implementation + "$" + role + ordinal++; while (!emittedTypes.Add(name));
                    return name;
                }
                void CaptureBody(BirClassDefinition lexical)
                {
                    var helpers = new List<BirFunctionDeclaration>();
                    callables.RewriteBody(lexical, lexical, exactParameters, plan.Slots, Allocate, generated, helpers);
                    if (helpers.Count != 0) lexical.Methods = lexical.Methods.OrEmpty().Concat(helpers).ToArray();
                }
                ProjectLexicalClosures(storage, plan.Exact, plan.Slots.Length, fullFrame, name => declarations[name].SingleOrDefault().type as BirClassDefinition,
                    Allocate, generated);
                foreach (var closure in generated.OfType<BirClassDefinition>().ToArray()) CaptureBody(closure);
                CaptureBody(storage);
                file.Types = file.Types.Value.Concat(generated).ToArray();
            }
            foreach (var method in storage.Methods.Value) method.RoundtripDeclarationIdentity = default;
            storage.Visit(node => {
                // A locally bound constructor keeps its overload index when its
                // declaring class is cloned. If a parameter comes from this
                // split frame, close the destination implementation with its
                // matching interface witnesses, not the weakened class slot.
                if (node is BirNew creation && creation.LocalCtorIndex.HasValue
                    && creation.Type.OrDefault() is TypeNode.Fqn { Args: { } arguments } selected
                    && storageOwners.TryGetValue(selected.Name, out var destination))
                {
                    bool forwarded = false;
                    var closed = entries[selected.Name].Close(arguments, (required, bound) => {
                        if (arguments[required.Primary] is TypeNode.Tv { Scope: "type" } source)
                        {
                            var slot = Array.FindIndex(plan.Slots, available => available.OwnerIndex == source.I
                                && available.Bound.Equals(bound));
                            if (slot >= 0)
                            {
                                forwarded = true;
                                return (TypeNode)new TypeNode.Tv("type", fullFrame.Length - plan.Slots.Length + slot);
                            }
                        }
                        return required.NativeArgument ? arguments[required.Primary] : bound;
                    });
                    if (forwarded) creation.Type = new TypeNode.Fqn(destination.Implementation, closed);
                }
                // A self-receiver now belongs to the private implementation, not to
                // an exact instantiation of the original Kotlin class. Other
                // instances and unrelated calls keep their original physical owner.
                var receiver = node switch {
                    BirFieldGet read => read.Recv.OrDefault(), BirSetField write => write.Recv.OrDefault(),
                    BirCallInstance call => call.Recv.OrDefault(), _ => null,
                };
                if (receiver is BirThis) node.RewriteOwnTypeFieldsInPlace((field, _, t) =>
                    field == "ownerType" && t is TypeNode.Fqn owner && owner.Name == plan.Exact ? Instance("type") : t);
            });
            var ctors = storage.Ctors.OrEmpty();
            for (int index = 0; index < ctors.Length; index++)
            {
                var constructor = ctors[index];
                var id = constructor.RoundtripDeclarationIdentity.OrDefault()?.DeclarationId.OrDefault();
                constructor.RoundtripDeclarationIdentity = default;
                if (id == null || !plan.Constructors.TryGetValue(id, out var factoryId)) continue;
                constructor.Vis = "internal";
                var factory = carrier.Methods.Value.Single(m =>
                    m.RoundtripDeclarationIdentity.OrDefault()?.DeclarationId.OrDefault() == factoryId);
                RoundtripMetadata.StampProjectedConstructor(factory, entries[plan.Exact]);
                if (plan.Outer != null)
                    innerFactories.Add((plan,id,factory,prefix,exactParameters.Length));
                if (referenceBuild) continue;
                var constructed = Instance("method");
                var creation = new BirNew { Type = constructed, LocalCtorIndex = index,
                    ArgTypes = constructor.Params.OrEmpty().Select(p => SupertypeGraph.SubstOwnerTvs(p.Type.Value, constructed.Args)).ToArray(),
                    Args = factory.Params.OrEmpty().Select(p => (IBirExpression)new BirLocal { Name = p.Name, Sty = p.Type }).ToArray(),
                };
                factory.Body = new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(creation) } };
            }
            file.Types = file.Types.Value.Append(storage).ToArray();
            emittedTypes.Add(plan.Implementation);
        }
        // The lexical receiver supplies the outer frame. Both implementations
        // need their own bridge, independent of TypeDef traversal order.
        foreach (var (inner,id,factory,prefix,original) in innerFactories)
        {
            var outerCarrier = declarations[inner.OuterCarrier].Single().type;
            MaterializeProjectedInnerFactory(declarations[inner.Outer].Single().type, outerCarrier,
                id, factory, inner, prefix, original, null, referenceBuild);
            if (implementations.TryGetValue(inner.Outer, out var outer))
                MaterializeProjectedInnerFactory(outer, outerCarrier, id, factory, inner, prefix, original,
                    storageOwners[inner.Outer], referenceBuild);
        }
        callables.Finish(documents, emittedTypes);
    }

    // Closure placement has already captured the enclosing CLR frame. Its copy
    // under a different declaring type needs that type's frame, followed by the
    // original lambda/method slots. The original closure remains untouched.
    internal static void ProjectLexicalClosures(BirClassDefinition storage, string originalName, int width, BirTypeParameter[] frame,
        Func<string, BirClassDefinition> definition, Func<string, string> allocate, List<IBirTypeDeclaration> generated)
    {
        var prefix = frame.Length - width;
        var names = new Dictionary<string, string>(StringComparer.Ordinal) { [originalName] = storage.Name.Value };
        TypeNode[] Extend(TypeNode[] args) => args.Take(prefix)
            .Concat(Enumerable.Range(prefix, width).Select(i => (TypeNode)new TypeNode.Tv("type", i)))
            .Concat(args.Skip(prefix)).ToArray();
        bool OwnPrefix(TypeNode[] args) => args?.Length >= prefix &&
            Enumerable.Range(0, prefix).All(i => args[i] is TypeNode.Tv { Scope: "type" } tv && tv.I == i);
        void Scan(BirClassDefinition owner, bool copied)
        {
            owner.Visit(node => {
                if (copied) node.RewriteOwnTypeFieldsInPlace((field, role, type) => {
                    if (node is BirMemberReference && field != "declaringType") return type;
                    var shifted = CapturedGenericInvocation.Map(type, tv =>
                        tv.Scope == "type" && tv.I >= prefix ? new TypeNode.Tv("type", tv.I + width) : tv);
                    return SourceDeclarationSignature.RewriteType(shifted, f =>
                        OwnPrefix(f.Args) && names.TryGetValue(f.Name, out var name)
                            ? new TypeNode.Fqn(name, Extend(f.Args)) : f);
                });
                var target = node switch { BirNewClosure c => c.ClosureType.OrDefault() as TypeNode.Fqn,
                    BirNewSam s => s.SamType.OrDefault() as TypeNode.Fqn, _ => null };
                if (target == null || node is not BirGraphNode creation ||
                    creation.ApplicationTypeArguments.OrDefault() is not { } args || !OwnPrefix(args) ||
                    definition(target.Name) is not { } original || original.NestedIn.OrDefault() is not { } parent ||
                    !names.TryGetValue(parent, out var projectedParent)) return;
                if (!names.TryGetValue(target.Name, out var projected))
                {
                    projected = allocate("closure");
                    names.Add(target.Name, projected);
                    var closure = (BirClassDefinition)original.DeepClone();
                    closure.Name = projected;
                    closure.NestedIn = projectedParent;
                    closure.RoundtripTypeHeaderPending = default;
                    closure.RoundtripSupertypesPre = default;
                    Scan(closure, true);
                    closure.CapturedTypeParams = frame.Select(p => (BirTypeParameter)p.DeepClone())
                        .Concat(closure.CapturedTypeParams.OrEmpty().Skip(prefix)).ToArray();
                    generated.Add(closure);
                }
                creation.ApplicationTypeArguments = Extend(args);
                if (creation is BirNewClosure lambda) lambda.ClosureType = new TypeNode.Fqn(projected);
                else ((BirNewSam)creation).SamType = new TypeNode.Fqn(projected);
            });
        }
        Scan(storage, false);
    }
}
