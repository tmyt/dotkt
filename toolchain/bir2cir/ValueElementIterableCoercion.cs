using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// KOTLIN COVARIANCE OVER A VALUE ELEMENT, AT AN OBJECT-ELEMENTED `Iterable` SLOT.
//
// Kotlin's `List<out E>` is covariant and `Int <: Int?`, so `List<Int>` IS an `Iterable<Int?>` and the frontend
// accepts `countNullable(listOf(1, 2, 3))` for `fun <T> countNullable(xs: Iterable<T?>)`. The CLR has no such
// relation: a reified generic argument is invariant for a value type, so an `IReadOnlyList<int32>` does not
// implement the `IEnumerable<object>` that slot erases to (#86), and the callee's `GetEnumerator` is not found.
//
// The conversion is `System.Linq.Enumerable.Cast<object>(this IEnumerable)`: every collection implements the
// NON-generic `IEnumerable`, and `Cast<object>` boxes each element — a `Nullable<V>` with no value boxing to a
// genuine `null` — yielding a real `IEnumerable<object>`.
//
// THE SLOT DECIDES, AND ONLY AN `Iterable` SLOT QUALIFIES. What the wrap produces is an `IEnumerable<object>` and
// nothing more, so it may only fill a slot that IS one: `kotlin.collections.Iterable<T?>`. A `List<T?>` slot is an
// `IReadOnlyList<object>` and a `Collection<T?>` slot an `IReadOnlyCollection<object>` — the wrap inhabits neither,
// and filling them with it is #324, where the conversion fired on a user generic's `List<A?>` parameter and the
// result did not inhabit the parameter at all.
//
// PER POSITION, judged by its own slot. The predicate is never accumulated across a parameter list: an unrelated
// `Box<T?>` parameter must not make an ordinary `Iterable<String>` argument convert, and an `Iterable<T?>` parameter
// in second place must not be missed because the first one is not.
//
// Runs on the substituted BIR BEFORE the erasure (the slot still says `Nullable(Tv)` and the call's `typeArgs` are
// still `kotlin.*`), and self-gates to concrete VALUE instantiations — an open `gp:T` argument is not a value type —
// so it is a no-op in the rt-stdlib self-build. A REFERENCE element needs nothing: covariance already works there.
static class ValueElementIterableCoercion
{
    // The struct-ness ORACLE (ReferenceMetadataIndex.IsValueTypeFqn + the local enum/struct types), not a hardcoded
    // primitive list: a Kotlin `value class` over a struct, a projected .NET struct and a local enum are value
    // elements for exactly the same CLR reason as `Int`, and a list that names only the primitives answers "no" for
    // them and silently drops the conversion.
    static Func<string, bool> _isValue = _ => false;

    // The one Kotlin collection head whose CLR form is `IEnumerable<E>` — the type the wrap produces.
    const string IterableFqn = "kotlin.collections.Iterable";

    // What `System.Linq.Enumerable.Cast<object>(IEnumerable)` produces — the STATIC TYPE the wrap below stamps on
    // itself. Spelled in the CLR vocabulary the wrap is already written in (its `memberSig` names the non-generic
    // `System.Collections.IEnumerable` the same way); BirTypeLowering passes a resolved BCL FQN through unchanged.
    static readonly TypeNode CastResultTn =
        new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new TypeNode[] { new TypeNode.Fqn("object") });

    public static void Apply(JsonNode root, Func<string, bool> isValue)
    {
        _isValue = isValue ?? (_ => false);
        Walk(root);
    }

    static void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                MaybeWrap(obj);
                foreach (var kv in obj) Walk(kv.Value);
                break;
            case JsonArray arr:
                foreach (var it in arr) Walk(it);
                break;
        }
    }

    static void MaybeWrap(JsonObject call)
    {
        if ((call["k"] as JsonValue)?.TryGetValue<string>(out var k) != true || k != "callStatic") return;
        if (call["sig"] is not JsonArray sig || call["args"] is not JsonArray args) return;   // sig is a structured TypeNode array (#37 m3b)
        if (sig.Count != args.Count) return;
        for (var i = 0; i < sig.Count; i++)
        {
            // The slot must be exactly `Iterable<T?>`: that is the only Kotlin type whose CLR form the wrap's
            // `IEnumerable<object>` inhabits.
            if (TypeJson.Read(sig[i]) is not TypeNode.Fqn { Name: IterableFqn, Args: { Length: 1 } sa }) continue;
            if (sa[0] is not TypeNode.Nullable { Of: TypeNode.Tv { Scope: "method" } tv }) continue;
            // WHICH type argument is the element: the index of the `Tv` under the slot's own `Nullable(Tv)`.
            // `filterNotNull()` declares `<T : Any>` so it is `typeArgs[0]`, but `filterNotNullTo(destination: C)`
            // declares `<C, T>` and it is `typeArgs[1]`; reading position 0 unconditionally answers about `C`, a
            // collection type and never a value.
            if (call["typeArgs"] is not JsonArray ta || tv.I < 0 || tv.I >= ta.Count) continue;
            if (!IsValueTypeArg(ta[tv.I])) continue;
            // Idempotence: never re-wrap an already-cast argument.
            if (args[i] is JsonObject ro && (ro["k"] as JsonValue)?.GetValue<string>() == "clrGenericStatic"
                && (ro["method"] as JsonValue)?.GetValue<string>() == "Cast") continue;
            args[i] = new JsonObject
            {
                ["k"] = "clrGenericStatic",
                ["type"] = TypeJson.Fqn("System.Linq.Enumerable"),
                ["method"] = "Cast",
                // typeArgs is a document type slot (ilemit MapType-resolves it) -> a structured `{t:fqn}` node.
                // `memberSig` (W1-S1 #46) is the FIR-resolved member descriptor: `Enumerable.Cast<TResult>(this
                // IEnumerable source)`'s DECLARED param is the non-generic `System.Collections.IEnumerable` — a
                // structured TypeNode ilemit exact-matches.
                ["typeArgs"] = new JsonArray { new JsonObject { ["t"] = "fqn", ["name"] = "object" } },
                ["memberSig"] = new JsonArray { new JsonObject { ["t"] = "fqn", ["name"] = "System.Collections.IEnumerable" } },
                ["args"] = new JsonArray { args[i].DeepClone() },
                // The wrap RETYPES the operand — `Cast<object>` turns a `List<Int>` into `IEnumerable<object>` — so
                // the new node is stamped with what IT produces, not with what the value it wraps used to be (spec
                // §2.7; the stamp is a claim about the value the node produces, and the two are unrelated invariant
                // reified generics, so the wrapped node's stamp would be a LIE here rather than an imprecision).
                // Unstamped, this node had no derivable static type at all — `bir-common/NodeType.cs` has no arm for
                // a `clr*` kind — and an operand with no static type left of a suspension is a stage-0 refusal of
                // source the frontend accepted (#304).
                ["sty"] = TypeJson.Write(CastResultTn),
            };
        }
    }

    // Is this type argument a value type, per the struct-ness oracle, on the pre-lowering structured Type node? A
    // CONSTRUCTED name is asked like any other — `KeyValuePair<K,V>` is a struct — and the oracle, which strips
    // generic arity, answers false for a constructed reference generic.
    static bool IsValueTypeArg(JsonNode n)
        => TypeJson.Read(n) is TypeNode.Fqn f && _isValue(f.Name);
}
