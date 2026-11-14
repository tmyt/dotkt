using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    static CarrierRealizationContext SourceSynthesisTestContext(BirFileDeclaration root, ReferenceMetadataIndex references)
    {
        var values = GenericRepresentationPolicy.CreateSource(new[] { root }, new Dictionary<string, string>(), references);
        var bindings = PrepareCurrentDeclarations(new[] { root }, values, out var preparation);
        var proofs = PrepareNativeProofs(bindings, references, values);
        var surface = PrepareCarrierSurface(preparation, proofs, references, values);
        return new(surface, CarrierProjectionContext.ForSource(surface.Demand, references), references, surface.Demand.ReservedNames);
    }

    internal static void SynthesisSourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning carrier synthesis: " + message); }
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Owning carrier synthesis admitted a non-owning proposal");
        }
        var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        try
        {
            var variable = new TypeNode.Tv("type", 0);
            var use = new TypeNode.Fqn("System.Collections.Generic.List", new TypeNode[] { variable });
            var text = new TypeNode.Fqn("kotlin.String");
            var defaultCarrier = DefaultBirCarrier.Create(1, new BirConstant {
                Type = text, Value = new BirLiteral.TextLiteral("opaque") });
            var bytes = ((BirOpaqueBytes)defaultCarrier.Args.Value[2]).Bytes.Value;
            var savedBytes = bytes.ToArray();
            var method = new BirFunctionDeclaration { Name = "apply", DeclarationId = "apply", Ret = use,
                Vis = "private", Params = new[] { new BirParameter { Name = "value", Type = use },
                    new BirParameter { Name = "label", Type = text, Attrs = new[] { defaultCarrier } } },
                Body = new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(new BirLocal { Name = "value" }) } },
                Mods = new BirModifiers { Suspend = true }, SuspendRet = use,
            };
            var body = method.Body.Value;
            var sourceParameters = method.Params.Value;
            var owner = new BirClassDefinition { Name = "Box", Kind = "class", Vis = "public",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } },
                Methods = new[] { method }, Interfaces = Array.Empty<TypeNode>() };
            var root = new BirFileDeclaration { FileClass = "CarrierTest", Types = new IBirTypeDeclaration[] { owner } };
            var context = SourceSynthesisTestContext(root, references);
            var selected = context.Owners["Box"];
            var prototype = InterfaceSlotSource(method, "$proposal", "Box", context);
            Reject(() => context.Bindings.GetCapturedSource(prototype));
            SynthesizeSource(selected, context);
            var row = context.Declarations.Slots(selected).Values.Single();
            var carrier = (BirInterfaceDefinition)root.Types.Value[1];
            var slot = carrier.Methods.Value.Single();
            var bridge = owner.Methods.Value[1];
            Require(ReferenceEquals(owner.Methods.Value[0], method) && ReferenceEquals(method.Body.Value, body)
                && ReferenceEquals(method.Params.Value, sourceParameters) && ReferenceEquals(row.Declaration, method)
                && ReferenceEquals(row.Original, context.Bindings.GetCapturedSource(method))
                && ReferenceEquals(row.Slot, slot) && ReferenceEquals(row.EmittedSlot, slot), "original occurrences or emitted row changed");
            Require(slot.Name.Value == "$star$apply$0" && slot.Abstract.Value && slot.Virtual.Value
                && slot.Vis.Value == "public" && !slot.Static.Value && !slot.Override.Value && !slot.ObjectOverride.Value
                && slot.Body.Value.Length == 0 && slot.Attrs.Value.Length == 1 && !slot.Generated.IsPresent
                && slot.DeclarationId.Value == ExistentialSlotIdentity("apply", "Box")
                && slot.SuspendRet.Value is TypeNode.Fqn { Name: "kotlin.Any" } && slot.Mods.Value.Suspend.Value,
                "slot identity, presence, result facts or separate naming domain changed");
            var sourceIdentity = slot.Attrs.Value.Single();
            Require(sourceIdentity.Attr.Value is TypeNode.Fqn { Name: "DotKt.Runtime.CompilerServices.KotlinSourceMethodAttribute" }
                && sourceIdentity.Args.Value[0] is BirAttributeLiteral { Value.Value: BirLiteral.TextLiteral { Value: BirCarrier.JsonV1 } }
                && System.Text.Encoding.UTF8.GetString(((BirOpaqueBytes)sourceIdentity.Args.Value[1]).Bytes.Value)
                    == "{\"name\":\"apply\",\"sourceDeclaration\":\"apply\"}",
                "renamed slot lost the shared source method identity carrier or inferred identity from its physical spelling");
            var call = (BirCallInstance)((BirReturn)bridge.Body.Value.Single()).Value.Value;
            Require(bridge.Generated.Value && bridge.PhysicalSlotBridge.Value && bridge.Vis.Value == "private"
                && bridge.ClrInterfaceImpls.Value.Single().Owner.Value == new TypeNode.Fqn("Box$star")
                && call.DeclarationId.Value == "apply" && call._exactExistentialBridgeOwnerCall.Value && call.ClrOwnerResolved.Value
                && call.Args.Value[0] is BirCast { _exactBridgeCast.Value: true, E.Value: BirLocal }
                && call.Sig.Value[0] == use && row.FrozenSignature[0] == use && call.SuspendCall.Value
                && !ReferenceEquals(((TypeNode.Fqn)row.FrozenSignature[0]).Args, use.Args)
                && ReferenceEquals(((BirOpaqueBytes)defaultCarrier.Args.Value[2]).Bytes.Value, bytes)
                && bytes.AsSpan().SequenceEqual(savedBytes)
                && !ReferenceEquals(((BirOpaqueBytes)slot.Params.Value[1].Attrs.Value[0].Args.Value[2]).Bytes.Value, bytes),
                "bridge target, exact casts or independent frozen source signature changed");
            var count = root.Types.Value.Length;
            SynthesizeSource(selected, context);
            Require(root.Types.Value.Length == count && owner.Methods.Value.Length == 2, "synthesis was not idempotent for an attached carrier");
            _ = root.ToDom(context.Bindings.ObserveProjection);
            context.Bindings.BindProjected();
            Require(context.Bindings.GetPhysical(row.Original.Physical) == row.Original, "one frontier lost the original binding");
            context.Declarations.ReleaseSourceRows();
        }
        finally { OwnerConstrainedMethodLowering.Reset(references); }

        try
        {
            var t = new TypeNode.Tv("type", 0);
            var outer = new BirClassDefinition { Name = "Outer", Kind = "class", Vis = "public",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = Array.Empty<BirFunctionDeclaration>() };
            var constructor = new BirConstructor { DeclarationId = "inner-constructor", Params = new[] {
                new BirParameter { Name = "outer", Outer = true, Type = new TypeNode.Fqn("Outer", new TypeNode[] { t }) },
                new BirParameter { Name = "value", Type = new TypeNode.Tv("type", 1) },
            }, Body = Array.Empty<IBirStatement>() };
            var inner = new BirClassDefinition { Name = "Outer.Inner", Kind = "class", SemanticOwner = "Outer", Vis = "public",
                Mods = new BirModifiers { Inner = true }, OuterTypeParamCount = 1,
                TypeParams = new[] { new BirTypeParameter { Name = "T" },
                    new BirTypeParameter { Name = "R", Constraints = new TypeNode[] { t } } },
                Methods = Array.Empty<BirFunctionDeclaration>(), Ctors = new[] { constructor } };
            var root = new BirFileDeclaration { FileClass = "InnerTest", Types = new IBirTypeDeclaration[] { outer, inner } };
            var context = SourceSynthesisTestContext(root, references);
            SynthesizeSource(context.Owners["Outer"], context);
            var row = context.Declarations.InnerFactories(context.Owners["Outer"]).Values.Single();
            var construction = (BirNew)((BirReturn)row.Bridge.Body.Value.Single()).Value.Value;
            Require(ReferenceEquals(row.Constructor, context.Bindings.GetCapturedSource(constructor))
                && row.TypeArgs.SequenceEqual(new[] { InnerFactoryBottomTypeArgument }) && !row.Slot.TypeParams.IsPresent
                && row.Slot.ExistentialInnerConstructorFactory.Value.Declaration.Value == "inner-constructor"
                && construction.ConstructorDeclarationId.Value == "inner-constructor"
                && construction.MemberSignature.Value[1] is TypeNode.Tv { Scope: "type", I: 1 }
                && ((TypeNode.Fqn)construction.Type.Value).Args[1] is TypeNode.Fqn { Name: "kotlin.Nothing" }
                && construction.Args.Value[0] is BirThis && row.Bridge.ClrInterfaceImpls.Value.Single().Arity.Value == 0,
                "inner factory lost its original constructor binding, bottom map or open declaration signature");
        }
        finally { OwnerConstrainedMethodLowering.Reset(references); }
        Console.WriteLine("[owning carrier synthesis] self-test OK (source occurrences, proposals/emission, bridge identity, copies and inner factory frame)");
    }
}
