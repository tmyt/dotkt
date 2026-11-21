using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal sealed record ProjectedStorageOwner(string Exact, string Carrier, string Implementation, string Outer, string OuterCarrier,
        ProjectedClassStorageFrame.InterfaceSlot[] Slots, IReadOnlyDictionary<string, string> Constructors);

    static string StorageFactoryId(string constructorId) =>
        DeclarationIdentityBinding.PhysicalOnlyId(constructorId, DeclarationIdentityBinding.ProjectedConstructorRole);

    static void RegisterProjectedStorage(Owner owner, IBirTypeDeclaration definition, BirInterfaceDefinition carrier,
        CarrierRealizationContext context)
    {
        if (definition is not BirClassDefinition || definition.Kind.OrDefault() != "class"
            || context.Projection.StorageFrame(owner.Name) is not { } frame) return;
        var constructors = definition.Ctors.OrEmpty().Where(c => c.DeclarationId.HasValue).ToArray();
        if (constructors.Length == 0) return;
        var used = carrier.Methods.OrEmpty().Select(method => method.Name.OrDefault()).ToHashSet(StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        int next = 0;
        foreach (var constructor in constructors)
        {
            var id = constructor.DeclarationId.Value;
            var factoryId = StorageFactoryId(id);
            string name;
            do name = "$projectedCtor$" + next++; while (!used.Add(name));
            ids.Add(id, factoryId);
            context.Declarations.ProjectedConstructors.Add(factoryId, (owner.ErasedName, name));
            var generic = frame.Declare();
            var methodFrame = Enumerable.Range(0, generic.Length).Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray();
            TypeNode InMethod(TypeNode type) => SupertypeGraph.SubstOwnerTvs(type, methodFrame);
            foreach (var parameter in generic) parameter.RewriteTypeFieldsInPlace(InMethod);
            var parameters = constructor.Params.OrEmpty().Select(parameter => (BirParameter)parameter.DeepClone()).ToArray();
            foreach (var parameter in parameters) parameter.RewriteTypeFieldsInPlace(InMethod);
            var factory = new BirFunctionDeclaration {
                Name = name, DeclarationSourceName = name, DeclarationId = factoryId,
                Static = true, Virtual = false, Override = false, Abstract = false, Vis = "public", Generated = true,
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
        if (carriers.ProjectedStorage.Count == 0) return;
        var declarations = documents.SelectMany(file => file.Types.OrEmpty().Select(type => (file, type)))
            .ToLookup(row => row.type.Name.OrDefault(), StringComparer.Ordinal);
        foreach (var plan in carriers.ProjectedStorage)
        {
            var (file, type) = declarations[plan.Exact].Single();
            var carrier = declarations[plan.Carrier].Single().type;
            var exact = (BirClassDefinition)type;
            var storage = (BirClassDefinition)exact.DeepClone();
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
            storage.Attrs = storage.Attrs.OrEmpty().Where(a =>
                a.Attr.OrDefault() is TypeNode.Fqn { Name: RoundtripMetadata.RuntimeClassifierAttribute }).ToArray();
            storage.Methods = storage.Methods.OrEmpty().Where(m => !m.Static.OrDefault()).ToArray();
            storage.Fields = storage.Fields.OrEmpty().Where(f => !f.Static.OrDefault()).ToArray();
            var count = fullFrame.Length;
            TypeNode.Fqn Instance(string scope) => new(plan.Implementation,
                Enumerable.Range(0, count).Select(i => (TypeNode)new TypeNode.Tv(scope, i)).ToArray());
            foreach (var method in storage.Methods.Value) method.RoundtripDeclarationIdentity = default;
            if (!referenceBuild)
            {
                var generated = new List<IBirTypeDeclaration>();
                var ordinal = 0;
                string Allocate(string role)
                {
                    string name;
                    do name = plan.Implementation + "$" + role + ordinal++; while (!emittedTypes.Add(name));
                    return name;
                }
                BirGraphValue Capture(BirGraphValue node)
                {
                    if (node is IBirExecutablePayload or BirMemberReference) return node;
                    node.VisitChildSlots(slot => slot.Replace(Capture(slot.Value)));
                    return CapturedGenericInvocation.ProjectStorageCall(node, plan.Slots, references, Allocate, generated);
                }
                Capture(storage);
                file.Types = file.Types.Value.Concat(generated).ToArray();
            }
            storage.Visit(node => {
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
                if (plan.Outer != null)
                    MaterializeProjectedInnerFactory(declarations[plan.Outer].Single().type, declarations[plan.OuterCarrier].Single().type,
                        id, factory, plan.Carrier, prefix, count, referenceBuild);
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
    }
}
