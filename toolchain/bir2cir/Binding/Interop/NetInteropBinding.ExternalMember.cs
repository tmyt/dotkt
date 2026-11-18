using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NetInteropBinding
{
    static void BindProjectedStaticFrame(SourceCall call)
    {
        var binding = call.ExternalMember.Value;
        var arguments = call.TypeArguments.OrEmpty();
        if (binding.Owner.Value.Type is not TypeNode.Fqn { Args.Length: > 0 } open)
            throw new InvalidOperationException("Projected static member requires a constructed declaring type");
        var methodArity = arguments.Length - open.Args.Length;
        if (methodArity < 0 || !open.Args.Select((argument, index) => argument is TypeNode.Tv { Scope: "method" } tv
                && tv.I == methodArity + index).All(match => match))
            throw new InvalidOperationException("Projected static member owner does not match its declared argument frame");
        var nativeFrame = arguments.Select((_, index) => (TypeNode)(index < methodArity
            ? new TypeNode.Tv("method", index) : new TypeNode.Tv("type", index - methodArity))).ToArray();
        call.Owner = FBoundStarProjectionErasure.SubstituteMethodTypeArguments(open, arguments);
        call.Method = binding.Member;
        call.TypeArguments = methodArity == 0 ? default : arguments[..methodArity];
        if (call.Shapes.HasValue) call.Shapes = call.Shapes.Value.Select(shape =>
            FBoundStarProjectionErasure.SubstituteMethodTypeArguments(shape, nativeFrame)).ToArray();
        ((BirCallStatic)call.Node).ExternalMember = default;
    }

    static BirGraphValue LowerSelectedExternalMember(SourceCall call, ReferenceMetadataIndex refs)
    {
        var binding = call.ExternalMember.Value;
        var sourceOwner = call.Owner.OrDefault() as TypeNode.Fqn
            ?? throw new InvalidOperationException("Selected external member has no declaration owner frame");
        var owner = SupertypeGraph.SubstOwnerTvs(binding.Owner.Value.Type,
            sourceOwner.Args ?? Array.Empty<TypeNode>()) as TypeNode.Fqn
            ?? throw new InvalidOperationException("Selected external member owner is not a named declaration");
        var open = ClrMemberResolution.ResolveOwnerType(owner, refs)
            ?? throw new InvalidOperationException("Selected external member owner does not resolve");
        // The projection selected this declaration, not an overload candidate.
        // In particular, two constructions can share its MethodDef token while
        // their explicit owner vectors remain distinct.
        var method = open.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(method => method.Name == binding.Member.Value);
        var reference = ClrMemberResolution.MemberRefOf(null, refs, method, MemberRefNode.Kinds.Method, open, owner.Args);
        // A reference receiver can be a subtype of its declaration owner. Its
        // value is interface-compatible, but &Subtype is not &DeclarationOwner.
        if (!refs.IsValueType(sourceOwner))
            return new BirNativeInstance {
                Type = owner, Method = method.Name,
                Recv = call.Receiver, Args = call.Arguments, Ret = call.Ret, Sty = call.Sty,
                Dispatch = "callvirt", Sig = reference.ParameterTypes, CalleeParams = reference.ParameterTypes,
                MemberRef = BirMemberReference.FromPhysical(reference), ResolvedMemberReturn = reference.ReturnType,
                SuspendCall = call.Suspend, _capturedGenericArgument = call.CapturedArguments,
                _capturedGenericResult = call.CapturedResult,
            };
        return new BirConstrainedCall {
            Iface = owner, RecvType = sourceOwner, Method = method.Name,
            Recv = call.Receiver, Args = call.Arguments, Ret = call.Ret, Sty = call.Sty,
            // These remain in the selected declaration's OPEN frame. Existing
            // call-slot consumers apply the explicit Iface arguments once.
            Sig = reference.ParameterTypes, CalleeParams = reference.ParameterTypes, CalleeRet = reference.ReturnType,
            MemberRef = BirMemberReference.FromPhysical(reference), ResolvedMemberReturn = reference.ReturnType,
            SuspendCall = call.Suspend, _capturedGenericArgument = call.CapturedArguments,
            _capturedGenericResult = call.CapturedResult,
        };
    }
}
