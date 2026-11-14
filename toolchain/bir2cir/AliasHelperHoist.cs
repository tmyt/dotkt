using DotKt.Bir;

// Rule-3 declaration synthesis is the representation boundary of CLR-bound
// aliases. Calls and declarations share the same owner/receiver policy below.
static partial class AliasHelperHoist
{
    internal const string IdentitySuffix = "|alias-helper";
    internal static string DeclarationIdentity(string sourceIdentity) => sourceIdentity + IdentitySuffix;

    internal static bool IsNativeArrayOwner(string owner) =>
        owner != null && (owner == "kotlin.Array" || BirTypeLowering.PrimArrayElem.ContainsKey(owner));

    internal static bool UsesNativeArrayObjectSlot(string owner, bool objectSlot) =>
        objectSlot && IsNativeArrayOwner(owner);

    static TypeNode RemapTypeVariables(TypeNode type, IReadOnlyDictionary<(string Scope, int Index), int> frame) =>
        RewriteType(type, variable => frame.TryGetValue((variable.Scope, variable.I), out var index)
            ? new TypeNode.Tv("method", index) : variable);

    static TypeNode RewriteType(TypeNode type, Func<TypeNode.Tv, TypeNode> rewrite) => type switch {
        TypeNode.Tv variable => rewrite(variable),
        TypeNode.Fqn value => new TypeNode.Fqn(value.Name, value.Args?.Select(argument => RewriteType(argument, rewrite)).ToArray()),
        TypeNode.Projection value => new TypeNode.Projection(value.Variance, RewriteType(value.Of, rewrite)),
        TypeNode.Nullable value => new TypeNode.Nullable(RewriteType(value.Of, rewrite)),
        TypeNode.Oblivious value => new TypeNode.Oblivious(RewriteType(value.Of, rewrite)),
        TypeNode.Array value => new TypeNode.Array(RewriteType(value.Elem, rewrite)),
        TypeNode.ByRef value => new TypeNode.ByRef(RewriteType(value.Of, rewrite)),
        TypeNode.Ptr value => new TypeNode.Ptr(RewriteType(value.Of, rewrite)),
        TypeNode.Mod value => new TypeNode.Mod(value.Req, RewriteType(value.M, rewrite), RewriteType(value.Of, rewrite)),
        TypeNode.Fn value => new TypeNode.Fn(value.Suspend, RewriteType(value.Ret, rewrite),
            value.Params.Select(parameter => RewriteType(parameter, rewrite)).ToArray(),
            value.Recv == null ? null : RewriteType(value.Recv, rewrite), value.Clr,
            value.Ctx?.Select(parameter => RewriteType(parameter, rewrite)).ToArray()),
        _ => type,
    };

    // Source frame roles, not arity or names, identify each ordinary/storage
    // counterpart. The declaration and the Rule-3 call descriptor use one rule.
    internal static TypeNode ReceiverType(string owner, NullableRepresentationFrame frame,
        GenericRepresentationPolicy representations)
    {
        if (owner == "kotlin.Array") return new TypeNode.Array(new TypeNode.Tv("type", 0));
        if (frame.SourceArity == 0) return new TypeNode.Fqn(owner);
        var source = Enumerable.Range(0, frame.SourceArity).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray();
        TypeNode Argument(TypeNode type, NullableRepresentationFrame.Role role) =>
            frame.Variable((TypeNode.Tv)type, representations.ApplicationRole(owner, role));
        return new TypeNode.Fqn(owner, frame.Close(source,
            type => Argument(type, NullableRepresentationFrame.Role.Ordinary),
            type => Argument(type, NullableRepresentationFrame.Role.Nullable),
            type => Argument(type, NullableRepresentationFrame.Role.Storage),
            type => Argument(type, NullableRepresentationFrame.Role.NullableStorage)));
    }
}
