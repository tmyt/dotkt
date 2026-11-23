using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class StringCharSequenceBridge
{
    const string CharSeq = "dotkt$CharSequence";
    const string Adapter = "dotkt$StringCharSequence";
    static readonly HashSet<string> StringTokens = new(StringComparer.Ordinal)
        { "kotlin.String", "System.String", "string" };
    // A StringBuilder is a non-String CharSequence that does NOT implement the synthetic `dotkt$CharSequence`; flowing
    // one into a CharSequence slot is snapshot to a String first. Every spelling the static
    // type can carry at this stage (the Kotlin typealias, the JDK class it aliases, the CLR type it lowers to).
    static readonly HashSet<string> StringBuilderTokens = new(StringComparer.Ordinal)
        { "kotlin.text.StringBuilder", "java.lang.StringBuilder", "java.lang.AbstractStringBuilder", "System.Text.StringBuilder" };

    // Process-wide: the app-local adapter type is emitted into EXACTLY ONE file's `types` per assembly (all of an app's
    // BIR files are lowered by a single bir2cir process; other files that also wrap resolve the type assembly-wide via
    // ilemit's `_types`). Fresh per process; non-reference builds only. `_fired` tracks whether a wrap was authored.
    static bool _adapterEmitted;
    static bool _fired;
    // #170 — lifted lambdas bound into a `dotkt$CharSequence`-returning delegate (from CharSeqStringLowering). Null-safe.
    static CharSeqStringLowering.CharSeqRetLambdas _retLambdas;

    internal static JsonObject RestoreMaterializedValue(JsonObject value, TypeNode source, TypeNode target) =>
        CanRestoreMaterializedValue(source, target)
            ? MaterializedExecutable.Normalize(RestoreErasedCharSequence(value, TypeJson.Write(target)).AsObject()) : null;

    internal static IBirExpression RestoreMaterializedValue(IBirExpression value, TypeNode source, TypeNode target) =>
        CanRestoreMaterializedValue(source, target)
            ? MaterializedExecutable.NormalizeSource(SourceRestoreErasedCharSequence(value, target)) : null;

    static bool CanRestoreMaterializedValue(TypeNode source, TypeNode target) =>
        source is TypeNode.Fqn { Name: "object" or "System.Object" or "kotlin.Any" } && IsScalarCharSequence(target);

    static TypeNode.Fn UnwrapFn(TypeNode t) => t switch
    {
        TypeNode.Fn f => f,
        TypeNode.Nullable { Of: TypeNode.Fn f } => f,
        TypeNode.Oblivious { Of: TypeNode.Fn f } => f,
        _ => null,
    };

    static bool SameDelegateParams(TypeNode.Fn source, TypeNode.Fn target)
    {
        var sp = source.DelegateParams;
        var tp = target.DelegateParams;
        if (sp.Length != tp.Length) return false;
        for (var i = 0; i < sp.Length; i++)
            if (sp[i] != tp[i]) return false;
        return true;
    }

    static TypeNode SubstCallTvs(TypeNode type, TypeNode[] ownerArgs, TypeNode[] methodArgs) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < ownerArgs.Length => ownerArgs[tv.I],
        TypeNode.Tv { Scope: "method" } tv when tv.I >= 0 && tv.I < methodArgs.Length => methodArgs[tv.I],
        TypeNode.Fqn { Args: { } args } f =>
            new TypeNode.Fqn(f.Name, args.Select(a => SubstCallTvs(a, ownerArgs, methodArgs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstCallTvs(n.Of, ownerArgs, methodArgs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstCallTvs(o.Of, ownerArgs, methodArgs)),
        TypeNode.Array a => new TypeNode.Array(SubstCallTvs(a.Elem, ownerArgs, methodArgs)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstCallTvs(b.Of, ownerArgs, methodArgs)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, SubstCallTvs(fn.Ret, ownerArgs, methodArgs),
            fn.Params.Select(p => SubstCallTvs(p, ownerArgs, methodArgs)).ToArray(),
            fn.Recv == null ? null : SubstCallTvs(fn.Recv, ownerArgs, methodArgs),
            fn.Clr, fn.Ctx?.Select(c => SubstCallTvs(c, ownerArgs, methodArgs)).ToArray()),
        _ => type,
    };

    static void CollectTvs(TypeNode type, List<TypeNode.Tv> result)
    {
        switch (type)
        {
            case TypeNode.Tv tv:
                if (!result.Contains(tv)) result.Add(tv);
                break;
            case TypeNode.Fqn { Args: { } args }:
                foreach (var a in args) CollectTvs(a, result);
                break;
            case TypeNode.Nullable n: CollectTvs(n.Of, result); break;
            case TypeNode.Oblivious o: CollectTvs(o.Of, result); break;
            case TypeNode.Array a: CollectTvs(a.Elem, result); break;
            case TypeNode.ByRef b: CollectTvs(b.Of, result); break;
            case TypeNode.Fn fn:
                CollectTvs(fn.Ret, result);
                foreach (var a in fn.DelegateParams) CollectTvs(a, result);
                break;
        }
    }

    static int _delegateAdapterCounter;

    enum CharSequenceConversion { None, String, StringBuilder, NullableString, NullableStringBuilder, Branches }

    // Whole-source and newly materialized-return traversal share the exact
    // positive conversion recognition/order over the same owning graph.
    static CharSequenceConversion SelectCharSequenceConversion(TypeNode type, bool nonNullSlot,
        bool notNullAssertion, bool conditional)
    {
        var stringType = (nonNullSlot || notNullAssertion) && type is TypeNode.Nullable nullable ? nullable.Of : type;
        if (IsStringTokT(stringType)) return CharSequenceConversion.String;
        var builderType = type switch { TypeNode.Nullable n => n.Of, TypeNode.Oblivious o => o.Of, _ => type };
        if (builderType is TypeNode.Fqn { Args: null } builder && StringBuilderTokens.Contains(builder.Name))
            return (type is TypeNode.Nullable or TypeNode.Oblivious) && !notNullAssertion
                ? CharSequenceConversion.NullableStringBuilder : CharSequenceConversion.StringBuilder;
        if (!nonNullSlot && type is TypeNode.Nullable optional && IsStringTokT(optional.Of))
            return CharSequenceConversion.NullableString;
        return conditional ? CharSequenceConversion.Branches : CharSequenceConversion.None;
    }

    // A structured Type is (nullable/array of) the CharSequence synthetic.
    static bool IsCharSeqT(TypeNode t) => t switch
    {
        TypeNode.Fqn f => f.Name == CharSeq,
        TypeNode.Nullable n => IsCharSeqT(n.Of),
        TypeNode.Array a => IsCharSeqT(a.Elem),
        _ => false,
    };
    static bool IsSourceCharSeqT(TypeNode t) => t switch
    {
        TypeNode.Fqn f => f.Name == "kotlin.CharSequence",
        TypeNode.Nullable n => IsSourceCharSeqT(n.Of),
        _ => false,
    };
    static bool IsScalarCharSequence(TypeNode type) => type switch
    {
        TypeNode.Fqn { Name: CharSeq } => true,
        TypeNode.Nullable nullable => IsScalarCharSequence(nullable.Of),
        TypeNode.Oblivious platform => IsScalarCharSequence(platform.Of),
        _ => false,
    };
    static bool IsStringTokT(TypeNode t) => t is TypeNode.Fqn { Args: null } f && StringTokens.Contains(f.Name);

    // Opaque carriers retain the original boxed value. Restore its CharSequence representation
    // at its owning consumption boundary, evaluating once and preserving null and existing views.
    static JsonNode RestoreErasedCharSequence(JsonNode value, JsonNode target)
    {
        var name = "__csrestore$" + System.Threading.Interlocked.Increment(ref _counter);
        JsonObject Read() => new() { ["k"] = "local", ["name"] = name };
        JsonObject Cast(string type) => new()
        {
            ["k"] = "cast", ["type"] = TypeJson.Fqn(type), ["e"] = Read(),
        };
        JsonObject Branch(string type, JsonNode converted, JsonNode otherwise) => new()
        {
            ["k"] = "cond", ["type"] = target.DeepClone(),
            ["cond"] = new JsonObject { ["k"] = "isInst", ["type"] = TypeJson.Fqn(type), ["e"] = Read() },
            ["then"] = converted, ["else"] = otherwise,
        };
        var unchanged = new JsonObject { ["k"] = "cast", ["type"] = target.DeepClone(), ["e"] = Read() };
        var snapshot = new JsonObject
        {
            ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Text.StringBuilder"),
            ["method"] = "ToString", ["ret"] = TypeJson.Fqn("System.String"),
            ["recv"] = Cast("System.Text.StringBuilder"), ["args"] = new JsonArray(),
        };
        var builder = Branch("System.Text.StringBuilder", WrapAdapter(snapshot), unchanged);
        return new JsonObject
        {
            ["k"] = "valueBlock",
            ["stmts"] = new JsonArray(new JsonObject
            {
                ["k"] = "var", ["name"] = name, ["type"] = TypeJson.Fqn("object"), ["init"] = value.DeepClone(),
            }),
            ["result"] = Branch("kotlin.String", WrapAdapter(Cast("kotlin.String")), builder),
        };
    }

    // The genuine late DOM result-restoration path refers to the app-local
    // adapter already supplied by owning source preparation. Downstream type
    // binding still resolves its Kotlin String constructor slot physically.
    static JsonObject WrapAdapter(JsonNode strExpr)
    {
        _fired = true;   // retain the shared adaptation state; source preparation owns declaration emission
        // Structured type slots (§1 — types are nodes): the adapter owner + the `kotlin.String` ctor-arg type as
        // `{t:"fqn",…}` nodes; BirTypeLowering lowers `kotlin.String` -> `System.String` downstream.
        return new JsonObject
        {
            ["k"] = "new",
            ["type"] = new JsonObject { ["t"] = "fqn", ["name"] = Adapter },
            ["argTypes"] = new JsonArray { new JsonObject { ["t"] = "fqn", ["name"] = "kotlin.String" } },
            ["args"] = new JsonArray { strExpr.DeepClone() },
        };
    }

    static int _counter;

}
