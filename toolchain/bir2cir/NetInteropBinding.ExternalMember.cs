using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NetInteropBinding
{
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
        var reference = ClrMemberResolution.MemberRefOf(refs, method, MemberRefNode.Kinds.Method, open, owner.Args);
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
