using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinOverrideSlotBridge
{
    internal static void SuspendSourceSelfTest()
    {
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var objectType = new TypeNode.Fqn("object");
        var unit = new TypeNode.Fqn("kotlin.Unit");
        BirOverride Edge(string owner, string id) => new() {
            Owner = new TypeNode.Fqn(owner), Member = "read", Kind = "method", DeclarationId = id,
        };
        BirInterfaceDefinition Slot(string name, string id) => new() {
            Kind = "interface", Name = name, Methods = new[] { new BirFunctionDeclaration {
                Name = "read", DeclarationId = id, Abstract = true, Params = Array.Empty<BirParameter>(), Ret = objectType,
                Mods = new BirModifiers { Suspend = true }, SuspendResult = objectType,
            } },
        };
        var first = Slot("SuspendSlotA", "slot-a");
        var second = Slot("SuspendSlotB", "slot-b");
        var originalParameters = Array.Empty<BirParameter>();
        var implementation = new BirFunctionDeclaration {
            Name = "read", DeclarationId = "source-read", Params = originalParameters, Ret = unit,
            Mods = new BirModifiers { Suspend = true }, SuspendRet = unit, SuspendResult = unit,
            Body = Array.Empty<IBirStatement>(), Overrides = new[] { Edge("SuspendSlotA", "slot-a"), Edge("SuspendSlotB", "slot-b") },
        };
        var owner = new BirClassDefinition { Kind = "class", Name = "SuspendImplementation",
            Interfaces = new TypeNode[] { new TypeNode.Fqn("SuspendSlotA"), new TypeNode.Fqn("SuspendSlotB") },
            Methods = new[] { implementation } };
        var file = new BirFileDeclaration { FileClass = "SuspendSourceProbe", Types = new IBirTypeDeclaration[] { owner, first, second } };
        var policy = GenericRepresentationPolicy.CreateSource(new[] { file }, new Dictionary<string, string>(), refs);
        var methods = new List<BirFunctionDeclaration>();
        var descriptors = new List<BirMethodImpl>();
        PrepareSuspendValueBridges(new[] { file }, _ => false, refs,
            new HashSet<string>(new[] { "SuspendSlotA", "SuspendSlotB", "SuspendImplementation" }), true, policy, null,
            new Dictionary<BirFunctionDeclaration, TypeNode[]>(ReferenceEqualityComparer.Instance) { [implementation] = [] },
            _ => false,
            (actualFile, actualOwner, method, descriptor) => {
                if (!ReferenceEquals(actualFile, file) || !ReferenceEquals(actualOwner, owner))
                    throw new InvalidOperationException("Suspend bridge callback lost the exact owner");
                if (method != null)
                {
                    if (!owner.Methods.Value.Any(value => ReferenceEquals(value, method)))
                        throw new InvalidOperationException("Suspend bridge callback preceded MethodDef attachment");
                    methods.Add(method);
                }
                if (descriptor is not BirMethodImpl exact || !owner.Methods.Value.Any(body =>
                    body.ClrInterfaceImpls.OrEmpty().Any(value => ReferenceEquals(value, exact))))
                    throw new InvalidOperationException("Suspend bridge callback published an unattached descriptor");
                descriptors.Add(exact);
            });
        var bridge = methods.Single();
        if (!ReferenceEquals(owner.Methods.Value[0], implementation) || !ReferenceEquals(implementation.Params.Value, originalParameters)
            || implementation.Ret.Value != unit || owner.Methods.Value.Length != 2 || descriptors.Count != 2
            || !descriptors.Select(value => ((TypeNode.Fqn)value.Owner.Value).Name).ToHashSet().SetEquals(new[] { "SuspendSlotA", "SuspendSlotB" })
            || bridge.TypeParams.IsPresent || descriptors.Any(value => value.TypeParams.IsPresent)
            || !bridge.SuspendTaskOnlyBridge.OrDefault() || bridge.SuspendResult.Value != unit
            || bridge.SuspendRet.Value != objectType || !implementation.Virtual.OrDefault())
            throw new InvalidOperationException("Suspend source bridge lost body sharing, presence, Unit result, or original declaration identity");
        var calls = new List<BirCallInstance>();
        void Visit(BirGraphValue value) { if (value is BirCallInstance call) calls.Add(call); value.VisitChildren(Visit); }
        Visit(bridge);
        var forwarded = calls.Single();
        if (!forwarded.SuspendCall.OrDefault() || !forwarded.Virtual.OrDefault() || !forwarded.ClrOwnerResolved.OrDefault()
            || forwarded.DeclarationId.Value != "source-read" || forwarded.Ret.Value != unit
            || forwarded.Sty.Value != unit || forwarded.DynRet.Value != unit || forwarded.CalleeParams.Value.Length != 0)
            throw new InvalidOperationException("Suspend source forwarder lost its exact selected call or complete result facts");

        var covariance = descriptors.Select(descriptor => new CovariantInterfaceReturnBridge.SourceBridgedSlot(implementation,
            CovariantInterfaceReturnBridge.DescriptorKey((TypeNode.Fqn)descriptor.Owner.Value, descriptor.Member.Value,
                descriptor.Arity.Value, descriptor.Params.Value, descriptor.Ret.Value, refs, _ => false))).ToHashSet();
        var before = owner.Methods.Value;
        PrepareSuspendValueBridges(new[] { file }, _ => false, refs, null, true, policy, covariance,
            new Dictionary<BirFunctionDeclaration, TypeNode[]>(ReferenceEqualityComparer.Instance) { [implementation] = [] }, _ => false);
        if (!ReferenceEquals(before, owner.Methods.Value))
            throw new InvalidOperationException("Suspend source preparation duplicated an exact covariant obligation");
        InheritedSelectionSourceSelfTest(refs);
        Console.WriteLine("[source suspend slots] self-test OK (actual append/descriptor callbacks, shared Unit body, covariant handles, selected inherited headers)");
    }

    static void InheritedSelectionSourceSelfTest(ReferenceMetadataIndex refs)
    {
        var variable = new TypeNode.Tv("type", 0);
        var text = new TypeNode.Fqn("kotlin.String");
        var source = new BirFunctionDeclaration { Name = "physicalRead", KotlinSourceMember = "read", DeclarationId = "base-read",
            Params = new[] { new BirParameter { Name = "value", Type = variable } }, Ret = variable,
            Body = Array.Empty<IBirStatement>() };
        var parent = new BirClassDefinition { Kind = "class", Name = "InheritedBase", TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { source } };
        var fact = new BirInheritedClassMethod {
            Member = "read", Params = new[] { new BirParameter { Name = "value", Type = text } }, Ret = text,
            Overrides = new[] { new BirOverride { Owner = new TypeNode.Fqn("InheritedBase", new[] { text }), Member = "read", Kind = "method", DeclarationId = "base-read" } },
            InheritedImplementation = new BirInheritedImplementation { Owner = new TypeNode.Fqn("InheritedBase", new[] { text }), Member = "read", Kind = "method", Arity = 0 },
        };
        var owner = new BirClassDefinition { Kind = "class", Name = "InheritedDerived", Base = new TypeNode.Fqn("InheritedBase", new[] { text }),
            InheritedClassMethods = new[] { fact }, Methods = Array.Empty<BirFunctionDeclaration>() };
        var file = new BirFileDeclaration { FileClass = "InheritedSelection", Types = new IBirTypeDeclaration[] { owner, parent } };
        var index = CreateSourceSelection(new[] { file });
        var selection = SelectedInheritedImplementationsSource(owner, index, refs, _ => false, (_, _) => true).Single();
        if (!ReferenceEquals(selection.Source, source) || !ReferenceEquals(selection.Fact, fact)
            || selection.Method.Params.Value.Single().Type.Value != text || selection.Method.Ret.Value != text
            || selection.DeclarationSignature.Single() != variable || selection.DeclarationId != "base-read"
            || selection.SelectionParameters.Single() != text || selection.Method.ExplicitClrName.Value != "physicalRead"
            || !ReferenceEquals(index.Declarations["InheritedDerived"], owner))
            throw new InvalidOperationException("Inherited selection lost current source identity, open declaration signature, or closed body frame");
        var bridge = BuildBridgeSource(owner, selection, new[] { text }, text, "selected-forwarder", _ => false, refs);
        var call = (BirCallInstance)((BirReturn)bridge.Body.Value.Single()).Value.Value;
        if (call.Method.Value != "physicalRead" || call.DeclarationId.Value != "base-read"
            || call.CalleeParams.Value.Single() != variable || call.Sig.Value.Single() != text
            || call.Ret.Value != text || ((TypeNode.Fqn)call.OwnerType.Value).Args.Single() != text)
            throw new InvalidOperationException("Inherited bridge confused a selected open signature with its closed physical body");
        if (source.Params.Value.Single().Type.Value != variable || fact.Params.Value.Single().Type.Value != text)
            throw new InvalidOperationException("Inherited selection rewrote its source or frontend fact");
    }
}
