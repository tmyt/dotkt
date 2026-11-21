using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    static string ProjectedInnerFactoryId(string constructorId) =>
        DeclarationIdentityBinding.PhysicalOnlyId(constructorId, DeclarationIdentityBinding.ProjectedInnerConstructorRole);

    // The outer object, not the caller, owns its captured CLR type arguments.
    // Expose only the inner frame on its virtual constructor slot.
    static void RegisterProjectedInnerFactory(Owner outer, Owner inner, BirConstructor ctor, SourceInnerFactory ordinary,
        CarrierRealizationContext context, List<BirFunctionDeclaration> methods, HashSet<string> used)
    {
        if (context.Projection.StorageFrame(inner.Name) is not { } layout) return;
        var captured = inner.OuterCount;
        TypeNode ForSlot(TypeNode t) => EraseOwnerTv(CapturedGenericInvocation.Map(t, tv => tv.Scope == "type" && tv.I >= captured
            ? new TypeNode.Tv("method", tv.I - captured) : tv), context.Owners, context.References);
        var frame = layout.Declare().Skip(captured).Select(p => CapturedGenericInvocation.CloneParameter(p, ForSlot)).ToArray();
        var parameters = ctor.Params.OrEmpty().Skip(1).Select(p => {
            var copy = (BirParameter)p.DeepClone();
            copy.RewriteTypeFieldsInPlace(ForSlot);
            return copy;
        }).ToArray();
        var id = ProjectedInnerFactoryId(ctor.DeclarationId.Value);
        var slot = new BirFunctionDeclaration {
            Name = ordinary.Slot.Name.Value + "$projected", Static = false, Abstract = true, Virtual = true, Vis = "public",
            DeclarationId = id, DeclarationSourceName = ordinary.Slot.Name.Value + "$projected",
            Generated = true, TypeParams = frame, Params = parameters, Ret = new TypeNode.Fqn(inner.ErasedName),
            Body = Array.Empty<IBirStatement>(), Attrs = Array.Empty<BirAttribute>(),
        };
        while (!used.Add(SourceMethodKey(slot))) { slot.Name = slot.Name.Value + "$"; slot.DeclarationSourceName = slot.Name; }
        methods.Add(slot);
        context.Declarations.ProjectedInnerFactories.Add(id, (outer.ErasedName, slot.Name.Value, frame.Length,
            parameters.Select(FunctionSignatureIdentity.SignatureType).ToArray(), slot.Ret.Value));
    }

    static void MaterializeProjectedInnerFactory(IBirTypeDeclaration outer, IBirTypeDeclaration outerCarrier,
        string ctorId, BirFunctionDeclaration factory, string innerCarrier, int prefix, int total, bool referenceBuild)
    {
        var id = ProjectedInnerFactoryId(ctorId);
        var slot = outerCarrier.Methods.Value.Single(m => m.RoundtripDeclarationIdentity.OrDefault()?.DeclarationId.OrDefault() == id);
        var bridge = (BirFunctionDeclaration)slot.DeepClone();
        bridge.RoundtripDeclarationIdentity = default;
        bridge.Abstract = false;
        bridge.Vis = "private";
        var parameters = slot.Params.OrEmpty().Select(FunctionSignatureIdentity.SignatureType).ToArray();
        var frame = slot.TypeParams.OrEmpty();
        bridge.ClrInterfaceImpls = new[] { new BirMethodImpl {
            Owner = new TypeNode.Fqn(outerCarrier.Name.Value), Member = slot.Name, Arity = frame.Length,
            TypeParams = frame.Select(p => (BirTypeParameter)p.DeepClone()).ToArray(), Params = parameters, Ret = slot.Ret,
        } };
        if (!referenceBuild)
        {
            var types = Enumerable.Range(0, total).Select(i => (TypeNode)(i < prefix ? new TypeNode.Tv("type", i)
                : new TypeNode.Tv("method", i-prefix))).ToArray();
            var signature = factory.Params.OrEmpty().Select(FunctionSignatureIdentity.SignatureType).ToArray();
            var args = new List<IBirExpression>{new BirThis()};
            for (int i=0; i < parameters.Length; i++)
            {
                IBirExpression arg = new BirLocal { Name = slot.Params.Value[i].Name, Sty = parameters[i] };
                var destination = SubstituteDeclarationTypeArguments(signature[i+1], Array.Empty<TypeNode>(), types);
                args.Add(parameters[i].Equals(destination) ? arg : CapturedGenericInvocation.Cast(destination, arg));
            }
            bridge.Body = new IBirStatement[] { new BirReturn { Value = new BirCallStatic {
                Owner = new TypeNode.Fqn(innerCarrier), Method = factory.Name, TypeArgs = types, Args = args.ToArray(),
                Sig = signature, CalleeParams = signature, Ret = factory.Ret, CalleeRet = factory.Ret,
            } } };
        }
        outer.Methods = outer.Methods.OrEmpty().Append(bridge).ToArray();
    }
}
