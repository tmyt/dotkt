using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CapturedGenericInvocation
{
    internal static BirGraphValue ProjectStorageCall(BirGenericStatic call,
        ProjectedClassStorageFrame.InterfaceSlot[] intersections, ReferenceMetadataIndex refs)
    {
        if (call.MemberRef.OrDefault() is not { } member || member.DeclaringType.OrDefault() is not TypeNode.Fqn owner
            || call.TypeArgs.OrDefault() is not { } arguments || call.Args.OrDefault() is not { } values) return call;
        var candidates = arguments.Select((argument, index) => (argument, index))
            .Where(row => row.argument is TypeNode.Tv { Scope: "type" } variable && intersections.Any(slot => slot.OwnerIndex == variable.I))
            .ToArray();
        if (candidates.Length == 0 || !refs.TryResolveStaticMemberSignature(owner.Name, member.Name.Value, arguments.Length,
            true, member.ParameterTypes.Value, owner.Args, out _, out var selected, out _)) return call;
        var generic = selected.GetGenericArguments();
        var missing = candidates.Where(row => generic[row.index].GetGenericParameterConstraints().Any(t => t.IsInterface))
            .Select(row => row.index).ToArray();
        if (missing.Length != 1) return call;
        var formal = selected.GetParameters();
        int witness = Array.FindIndex(formal, parameter => parameter.ParameterType == generic[missing[0]]);
        if (witness < 0 || formal.Any(p => p.ParameterType.IsByRef || p.ParameterType.IsByRefLike)
            || selected.ReturnType.IsByRef || selected.ReturnType.IsByRefLike) return call;

        // The original native method is selected at compile time. Open only
        // its type argument on the actual object at the call boundary: the
        // projected class's primary slot guarantees the class, not its interfaces.
        var unit = selected.ReturnType.FullName == "System.Void";
        var name = unit ? "starProjectionInvokeStaticHelperUnit" : "starProjectionInvokeStaticHelper";
        var runtimeType = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionType");
        var lookup = new TypeNode[] { runtimeType, new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.String"),
            new TypeNode.Array(new TypeNode.Fqn("kotlin.String")), new TypeNode.Array(runtimeType), new TypeNode.Fqn("kotlin.Int"),
            new TypeNode.Array(new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"))) };
        if (!refs.TryResolveStaticMemberSignature(RuntimeOwner, name, 0, true, lookup, null, out _, out var helper, out var declaring))
            throw new InvalidOperationException($"Projected invocation has no runtime binding for '{name}'");
        var target = BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(null, refs, helper, "method", declaring, null));
        var invoke = new BirNativeStatic {
            Type = new TypeNode.Fqn(RuntimeOwner), Method = name, MemberRef = target,
            Ret = target.ReturnType, ResolvedMemberReturn = target.ReturnType,
            Args = new IBirExpression[] { ClassRef(owner), Number(selected.MetadataToken), Text(selected.Name),
                NewArray(String, formal.Select(p => (IBirExpression)Text(ReferenceMetadataIndex.ForeignStarRuntimeTypeKey(p.ParameterType)))),
                NewArray(Type, arguments.Select(t => (IBirExpression)ClassRef(t))), Number(witness), NewArray(Object, values) },
        };
        ForeignStarProjectionBinding.RequireRuntimeFallback();
        var result = FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(member.ReturnType.Value, owner.Args ?? [], arguments);
        return unit || IsObject(result) ? invoke : Cast(result, invoke);
    }
}
