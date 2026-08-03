using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// AN ARGUMENT-ABANDONING VALUE MEETING A CONSTRUCTED GENERIC PARAMETER.
//
// `List<*>.firstOrNull()` / `Array<*>.firstOrNull()` are ordinary generic stdlib functions whose type argument the
// frontend infers from the captured projection, i.e. `Any?`. So the callee's parameter is `IReadOnlyList<object>`
// / `object[]` while the value is a `List<int32>` / `int32[]`. Neither is assignment-compatible with the other —
// value-type arguments get no covariance, and array covariance is reference-element only — so the call boundary is
// unverifiable and the callee faults on its first element access (measured: EntryPointNotFound for the collection
// form, an AccessViolation that aborts the process for the array form, because `int32[]` read as `object[]` is a
// raw reinterpret of element storage rather than a failed cast).
//
// The physical view fixes how the value is STORED; it cannot make the value inhabit a reified instantiation it
// does not. The only sound conversion is a BOXING one, and it has to be materialized: `clrStarToList` /
// `clrStarToArray` copy the elements through the non-generic enumerator, boxing each. Kotlin's `List<T>`,
// `Collection<T>`, `Iterable<T>` and `Sequence<T>` receivers are READ-ONLY, so a snapshot is observationally
// identical for them; the mutable aliases are deliberately excluded, because a snapshot would silently swallow a
// write. `Map` is excluded too — a boxed dictionary snapshot is a separate conversion, and no measured program
// needs it.
//
// Runs in BIR space, before type lowering: `sig` still names the callee's DECLARED parameter types with their
// method type variables, `typeArgs` still names the instantiation, and the argument still carries the `sty` stamp
// that identifies it as a projection. After lowering all three facts are gone.
static class StarViewArgCoercion
{
    const string Helpers = "kotlin.collections.ClrStarProjectionKt";

    // The READ-ONLY Kotlin collection aliases a boxed `List<Any?>` snapshot legitimately fills. Every one of them
    // lowers to a BCL interface that the snapshot's `List<object>` implements (IEnumerable/IReadOnlyCollection/
    // IReadOnlyList at `object`). The mutable siblings and `Set` are absent on purpose: a snapshot is not a live
    // view, and a list is not a set.
    static readonly HashSet<string> SnapshotFillable = new(StringComparer.Ordinal)
    {
        "kotlin.collections.List",
        "kotlin.collections.Collection",
        "kotlin.collections.Iterable",
        "kotlin.sequences.Sequence",
    };

    public static void Apply(JsonNode root) => Walk(root);

    static void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                Coerce(obj);
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                    if (obj[key] is JsonNode child) Walk(child);
                break;
            case JsonArray arr:
                foreach (var item in arr) if (item != null) Walk(item);
                break;
        }
    }

    static void Coerce(JsonObject call)
    {
        if (Str(call["k"]) is not ("callStatic" or "callInstance")
            || call["args"] is not JsonArray args
            || call["sig"] is not JsonArray sig
            || args.Count != sig.Count) return;

        var typeArgs = (call["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray() ?? Array.Empty<TypeNode>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is not JsonObject arg || !Abandons(TypeJson.Read(arg["sty"]))) continue;
            if (TypeJson.Read(sig[i]) is not TypeNode declared) continue;
            var want = Substitute(declared, typeArgs);
            if (Helper(want) is not (string method, TypeNode result)) continue;
            args[i] = new JsonObject
            {
                ["sty"] = TypeJson.Write(result),
                ["k"] = "callStatic",
                ["owner"] = TypeJson.Fqn(Helpers),
                ["method"] = method,
                ["args"] = new JsonArray { arg.DeepClone() },
            };
        }
    }

    // The materializing helper for a parameter the projection cannot inhabit, plus the type it produces (which
    // becomes the wrapper's own `sty`). Null when the parameter is not one of the two shapes a boxed snapshot
    // fills — an `Any?` parameter, a mutable collection, a map — in which case the argument is left alone.
    static (string Method, TypeNode Result)? Helper(TypeNode want) => want switch
    {
        TypeNode.Array a when IsObjectish(a.Elem) =>
            ("clrStarToArray", new TypeNode.Array(new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")))),
        TypeNode.Fqn { Args: { Length: 1 } wa } f when SnapshotFillable.Contains(f.Name) && IsObjectish(wa[0]) =>
            ("clrStarToList", new TypeNode.Fqn("kotlin.collections.List",
                new TypeNode[] { new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")) })),
        _ => null,
    };

    // The argument's frontend static type abandons a type argument: `Array<*>`, or a constructed generic with a
    // star among its arguments. This is the same fact StarProjectionView keys the slot on.
    static bool Abandons(TypeNode t) => t switch
    {
        TypeNode.Array a => a.Elem is TypeNode.Star,
        TypeNode.Fqn { Args: { } args } => args.Any(a => a is TypeNode.Star),
        TypeNode.Nullable n => Abandons(n.Of),
        TypeNode.Oblivious o => Abandons(o.Of),
        _ => false,
    };

    static bool IsObjectish(TypeNode t) => t switch
    {
        TypeNode.Nullable n => IsObjectish(n.Of),
        TypeNode.Oblivious o => IsObjectish(o.Of),
        TypeNode.Star => true,
        TypeNode.Fqn { Args: null, Name: "kotlin.Any" or "object" or "System.Object" } => true,
        _ => false,
    };

    static TypeNode Substitute(TypeNode type, TypeNode[] args) => type switch
    {
        TypeNode.Tv { Scope: "method" } tv when tv.I >= 0 && tv.I < args.Length => args[tv.I],
        TypeNode.Fqn f when f.Args is not null =>
            new TypeNode.Fqn(f.Name, f.Args.Select(a => Substitute(a, args)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(Substitute(n.Of, args)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Substitute(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(Substitute(a.Elem, args)),
        TypeNode.ByRef b => new TypeNode.ByRef(Substitute(b.Of, args)),
        _ => type,
    };

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
