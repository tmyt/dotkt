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
        var full = layout.Declare();
        var indices = layout.Constructor(captured).InnerSlots();
        // The erased outer has no !i for an enclosing type parameter. An
        // instance slot weakened to object could not call the constrained
        // static factory. The ordinary bottom-specialized entry still exists.
        var capturedSlots = Enumerable.Range(0, full.Length).Except(indices).ToHashSet();
        if (indices.Any(i => full[i].Constraints.OrEmpty().Any(c =>
                ContainsInnerDependency(c, captured, capturedSlots)))) return;
        TypeNode ForSlot(TypeNode t) => EraseOwnerTv(CapturedGenericInvocation.Map(t, tv => tv.Scope == "type" &&
            Array.IndexOf(indices, tv.I) is var i && i >= 0 ? new TypeNode.Tv("method", i) : tv), context.Owners, context.References);
        var frame = indices.Select(i => CapturedGenericInvocation.CloneParameter(full[i], ForSlot)).ToArray();
        var parameters = ctor.Params.OrEmpty().Skip(1).Select(p => {
            var copy = (BirParameter)p.DeepClone();
            copy.RewriteTypeFieldsInPlace(ForSlot);
            return copy;
        }).ToArray();
        var id = ProjectedInnerFactoryId(ctor.DeclarationId.Value);
        var slot = new BirFunctionDeclaration {
            Name = ordinary.Slot.Name.Value + "$projected", Static = false, Abstract = true, Virtual = true, Vis = FactoryVisibility(ctor),
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
        string ctorId, BirFunctionDeclaration factory, ProjectedStorageOwner inner, int prefix, int original,
        ProjectedStorageOwner projectedOuter, bool referenceBuild)
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
            var indices = inner.Entry.InnerSlots();
            var types = Enumerable.Range(0, original + inner.Slots.Length).Select(i => {
                if (Array.IndexOf(indices, i) is var methodIndex && methodIndex >= 0)
                    return (TypeNode)new TypeNode.Tv("method", methodIndex);
                if (i < prefix) return new TypeNode.Tv("type", i);
                var facet = inner.Slots[i - original];
                var split = projectedOuter == null ? -1 : Array.FindIndex(projectedOuter.Slots,
                    s => s.OwnerIndex == facet.OwnerIndex && s.Bound.Equals(facet.Bound));
                return new TypeNode.Tv("type", split < 0 ? facet.OwnerIndex : prefix + split);
            }).ToArray();
            var signature = factory.Params.OrEmpty().Select(FunctionSignatureIdentity.SignatureType).ToArray();
            var args = new List<IBirExpression>{new BirThis()};
            for (int i=0; i < parameters.Length; i++)
            {
                IBirExpression arg = new BirLocal { Name = slot.Params.Value[i].Name, Sty = parameters[i] };
                var destination = SubstituteDeclarationTypeArguments(signature[i+1], Array.Empty<TypeNode>(), types);
                args.Add(parameters[i].Equals(destination) ? arg : CapturedGenericInvocation.Cast(destination, arg));
            }
            bridge.Body = new IBirStatement[] { new BirReturn { Value = new BirCallStatic {
                Owner = new TypeNode.Fqn(inner.Carrier), Method = factory.Name, TypeArgs = types, Args = args.ToArray(),
                Sig = signature, CalleeParams = signature, Ret = factory.Ret, CalleeRet = factory.Ret,
            } } };
        }
        outer.Methods = outer.Methods.OrEmpty().Append(bridge).ToArray();
    }
}
