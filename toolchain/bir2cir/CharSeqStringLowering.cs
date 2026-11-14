using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// A pure application's owning source pass selects String snapshots for its
// CharSequence declarations. External signatures retain their declared view;
// StringCharSequenceBridge materializes that boundary after member binding.
static partial class CharSeqStringLowering
{
    const string CharSeq = "dotkt$CharSequence";
    static readonly TypeNode StringTn = new TypeNode.Fqn("kotlin.String");
    static readonly HashSet<string> StringTokens = new(StringComparer.Ordinal)
        { "kotlin.String", "System.String", "string" };

    // These allocation clocks span applications/files of this pass. Receiver
    // and start spills, and nullable snapshot spills, have independent names.
    static int _subSeqTmp;
    static int _nullableSnapshotTmp;

    static bool IsCharSeqT(TypeNode type) => type switch {
        TypeNode.Fqn value => value.Name == CharSeq,
        TypeNode.Nullable value => IsCharSeqT(value.Of),
        TypeNode.Array value => IsCharSeqT(value.Elem),
        _ => false,
    };

    // This is a selected slot rewrite, not a recursive classifier rewrite:
    // nullable/array wrappers survive and unrelated nested arguments stay put.
    static TypeNode LowerTokT(TypeNode type) => type switch {
        TypeNode.Nullable value => new TypeNode.Nullable(LowerTokT(value.Of)),
        TypeNode.Array value => new TypeNode.Array(LowerTokT(value.Elem)),
        _ => StringTn,
    };

    // Facts collected before collapse, consumed by the actual later bridge to
    // restore lifted method/closure returns to their declared delegate view.
    public sealed record CharSeqRetLambdas(HashSet<string> Statics, HashSet<string> Closures);

    // Nullable returns are a separate boundary. Only a bare CharSequence return
    // participates in this lifted-return handoff.
    static bool FuncTypeHasCharSeqRet(TypeNode type)
        => type is TypeNode.Fn { Ret: TypeNode.Fqn { Name: CharSeq } };

    static bool FuncTypeHasCharSeqParam(TypeNode type)
        => type is TypeNode.Fn function && function.DelegateParams.Any(IsCharSeqT);

    static bool IsStringTokT(TypeNode type)
        => type is TypeNode.Fqn { Args: null } value && StringTokens.Contains(value.Name);

    static bool IsStringDeclT(TypeNode type) => IsStringTokT(type)
        || type is TypeNode.Nullable nullable && IsStringTokT(nullable.Of);
}
