using System.Collections.Generic;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A `kotlin.reflect.KClass` member read (`T::class.simpleName` / `.qualifiedName`) -> the Kotlin name.
// kotc emits the pure-Kotlin property read `callInstance(kotlin.reflect.KClass[..].get_simpleName, recv = <::class>)`.
// The `::class` receiver is a System.Type token, emitted by kotc as either:
//   • `classRef`  — an UNBOUND class literal on a type (`Int::class`, `Foo::class`, `Box::class`, a reified `T::class`
//     after inline-splice substitution): `{"k":"classRef","type":<Kotlin FQN>}` = a static `typeof`.
//   • `getType`   — a BOUND class reference on a value (`1::class`, `"x"::class`, `x::class`): `{"k":"getType","e":<expr>}`
//     = a run-time `<expr>.GetType()`, reflecting the value's RUNTIME class.
// KClass is @ClrTypeAlias-ed onto System.Type. This pass owns the Kotlin<->CLR NAME reversal (#138). Because it runs
// BEFORE BirTypeLowering, every `type`/`sty` slot is still a pure Kotlin FQN (`kotlin.Int`, `kotlin.String`, a user
// FQN), so where the Kotlin type identity is statically known it CONST-FOLDS the accessor straight off that token —
// `qualifiedName` = the FQN verbatim, `simpleName` = its last `.`-segment. No CLR->Kotlin reverse TABLE is needed: the
// Kotlin identity is still in hand. Foldable cases:
//   • classRef — ALWAYS: the token IS the literal type (`Int::class` -> "Int"/"kotlin.Int", `Box::class` -> "Box").
//   • getType — when the argument's static type is a KNOWN-FINAL builtin (the primitive tower, String, the specialized
//     arrays, Unit/Nothing) AND the argument is side-effect-free (a `const`/`local`, NOT a wrapper/call — see below). A
//     final type has no subtypes, so the RUNTIME class == the static type;
//     `1::class` -> "Int"/"kotlin.Int", `"x"::class` -> "String"/"kotlin.String" — the reported failures. (Not folding a
//     side-effecting `foo()::class` receiver keeps `foo()`'s evaluation.)
// Everything else keeps the faithful run-time `System.Type.Name`/`.FullName` read:
//   • a BOUND `getType` on an OPEN/interface static type (`x: Any`, `list: List<Int>`) — the runtime class is a subtype,
//     so the name is genuinely dynamic. Closing that (CLR-renamed builtins + generic backtick mangling surfacing at run
//     time) needs a run-time CLR->Kotlin reverse-map HELPER (a stdlib runtime fn this pass would route to) — §5g of
//     docs/dotkt-semantics.md, a sequenced cross-layer stdlib follow-up, NOT a bir2cir-only const-fold.
//   • a BOUND `getType` on a FINAL USER class — for a NON-GENERIC, TOP-LEVEL user type `Type.FullName`/`.Name` already
//     ARE the Kotlin qualified/simple name (no CLR rename), so the run-time read is correct. A GENERIC user class reads
//     back backtick-mangled (`Box`1`) and a NESTED one uses the CLR `+` separator (`Outer+Inner`); those two shapes join
//     the §5g dynamic follow-up (an UNBOUND `Box::class`/`Outer.Inner::class` is already exact via the classRef fold).
//   • a BOUND `getType` whose receiver is a SMART-CAST wrapper (`cast`/`nullableValue`) or a bare `this` — not folded
//     (a `cast` node also carries a throwing explicit `as`, so it is not safe to drop); §5g dynamic follow-up.
// Non-ref only (the ref stdlib keeps KClass pure Kotlin). Bottom-up rewrite, mirroring ClrEventOperatorBinding.
static class KClassMemberBinding
{
    // Kotlin builtin types that are FINAL (no subtypes), so a bound `value::class` on one is statically resolvable
    // (runtime class == static type) and const-folding to the Kotlin name is sound. Most are also CLR-RENAMED (the
    // primitive tower -> System.Int32/…, String -> System.String, the specialized arrays -> int[]/…), so the fold is
    // what restores the Kotlin name; Unit/Nothing keep their Kotlin name but are included so a `Unit` local's `::class`
    // folds uniformly. (`kotlin.Array<T>` is a distinct `{t:array}` token, not an Fqn — its bound case is a follow-up.)
    static readonly HashSet<string> KnownFinal = new()
    {
        "kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte",
        "kotlin.UInt", "kotlin.ULong", "kotlin.UShort", "kotlin.UByte",
        "kotlin.Double", "kotlin.Float", "kotlin.Boolean", "kotlin.Char",
        "kotlin.String", "kotlin.Unit", "kotlin.Nothing",
        "kotlin.IntArray", "kotlin.LongArray", "kotlin.ShortArray", "kotlin.ByteArray",
        "kotlin.DoubleArray", "kotlin.FloatArray", "kotlin.BooleanArray", "kotlin.CharArray",
        "kotlin.UIntArray", "kotlin.ULongArray", "kotlin.UShortArray", "kotlin.UByteArray",
    };

    public static JsonNode Apply(JsonNode root) => Walk(root);

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    static JsonNode Walk(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var copy = new JsonObject();
            foreach (var kv in obj) copy[kv.Key] = kv.Value == null ? null : Walk(kv.Value);   // children first (bottom-up)
            return Transform(copy) ?? copy;
        }
        if (node is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr) copy.Add(item == null ? null : Walk(item));
            return copy;
        }
        return node.DeepClone();
    }

    static JsonNode Transform(JsonObject node)
    {
        if (Str(node["k"]) != "callInstance") return null;
        // ownerType is `kotlin.reflect.KClass` (its type-arg, if any, is dropped by OwnerName — we key on the identity).
        if (TypeJson.OwnerName(node["ownerType"]) != "kotlin.reflect.KClass") return null;
        var member = Str(node["method"]);
        bool simple = member == "get_simpleName";
        if (!simple && member != "get_qualifiedName") return null;
        if (node["recv"] is not JsonObject recv) return null;   // the ::class receiver (a System.Type value)

        // Const-fold when the receiver's Kotlin type identity is statically known (see the file header).
        if (StaticKotlinFqn(recv) is string fqn)
        {
            return new JsonObject
            {
                ["k"] = "const",
                ["type"] = TypeJson.Fqn("kotlin.String"),
                ["value"] = simple ? SimpleOf(fqn) : fqn,
            };
        }

        // Dynamic — keep the faithful System.Type reflection read (§5g: the CLR->Kotlin run-time helper is a sequenced
        // stdlib follow-up). KClass is @ClrTypeAlias-ed onto System.Type, so bind the BCL member.
        return new JsonObject
        {
            ["k"] = "clrPropGet",
            ["type"] = TypeJson.Fqn("System.Type"),
            ["name"] = simple ? "Name" : "FullName",
            ["ret"] = TypeJson.Fqn("System.String"),
            ["static"] = false,
            ["recv"] = recv.DeepClone(),
        };
    }

    // The statically-known Kotlin FQN a `::class` receiver names, or null when the class is only known at run time.
    static string StaticKotlinFqn(JsonObject recv) => Str(recv["k"]) switch
    {
        // Unbound class literal — the token IS the literal type; always resolvable (finality is irrelevant, it is a type,
        // not a reflected instance).
        "classRef" => FqnOf(recv["type"]),
        // Bound `value::class` — resolvable iff the value's static type is a known-final builtin (runtime class == static
        // type) AND the value is side-effect-free, so folding away the receiver drops no evaluation.
        "getType" when recv["e"] is JsonObject e && (Str(e["k"]) is "const" or "local")
            && FqnOf(e["sty"] ?? e["type"]) is string st && KnownFinal.Contains(st) => st,
        _ => null,
    };

    // The Kotlin FQN a type slot names — the bare Fqn, or a nullable-of-Fqn unwrapped to its core (an inline-spliced
    // `T::class` with a nullable T arg, or a `String?` receiver; the Kotlin answer is the non-null identity's name).
    // Null for any other token shape (tv/array/fn), which the caller routes to the run-time read instead of folding.
    static string FqnOf(JsonNode typeNode) => TypeJson.Read(typeNode) switch
    {
        TypeNode.Fqn f => f.Name,
        TypeNode.Nullable { Of: TypeNode.Fqn f } => f.Name,
        _ => null,
    };

    // simpleName = the segment after the last `.` (a nested class's `Outer.Inner` -> `Inner`); the whole name otherwise.
    static string SimpleOf(string fqn)
    {
        int dot = fqn.LastIndexOf('.');
        return dot >= 0 ? fqn.Substring(dot + 1) : fqn;
    }
}
