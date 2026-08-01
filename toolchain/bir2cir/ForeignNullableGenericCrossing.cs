using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// THE ONE SHAPE CARRIER-ARGUMENT ERASURE CANNOT MEET (#86).
//
// Kotlin's `X?` for a possibly-value `X` is `System.Object` in every reified ARGUMENT, so a Kotlin `List<Int?>` is an
// `IReadOnlyList<object>` and there is no Kotlin type whose physical form is `List<Nullable<int32>>`. A .NET API may
// nevertheless DECLARE one — `List<int?>`, `Dictionary<string, int?>`, `int?[]`, `Func<int?, string>` — and a
// resolved foreign declaration is authoritative: NullableGenericErasure does not restate what a CLR member declares.
//
// The two therefore do not meet, and neither side can be bent to the other:
//   * `List<object>` and `List<Nullable<int32>>` are unrelated INVARIANT reified generics; no `castclass` joins them
//     (one throws) and no covariance relates them, because a reified value-type argument has none.
//   * Adapting silently — copying into a fresh `List<int?>` at the call, or wrapping — would give the argument
//     different identity and different mutation semantics than the Kotlin source says it has. An adapter that
//     changes what `xs.add(1)` does to the caller's list is a wrong program, not a bridge.
//
// So the crossing is REFUSED, naming the member and the slot. That is the same discipline as the other refusals in
// this family: a program with no valid CIL lowering owes its author an actionable message rather than a silently
// different meaning. The refusal is narrow by construction — it needs a .NET member whose signature genuinely nests
// a `Nullable<V>` inside a reified argument, which the BCL surface almost never does — and a DIRECT `Nullable<V>`
// parameter or return is untouched, because a Kotlin scalar `Int?` IS a `System.Nullable<int32>` and crosses exactly.
//
// WHICH NODES ARE ASKED is the presence of a stamped .NET declaration (`memberSig`/`memberRet`), not a list of node
// kinds: those keys exist on exactly the nodes ClrMemberResolution resolved, so the trigger cannot drift from the
// stamping. That reaches a bound method reference, an event accessor and an accessor-backed external field — each of
// which carries a declaration and none of which a kind list assembled by hand had included.
//
// WHICH POSITIONS COUNT is not decided here. `NullableGenericErasure.ErasureWouldMove` answers it, beside the `Erase`
// it has to agree with position for position: a delegate PARAMETER keeps a concrete `V?` in that rule, so a foreign
// `Func<int?, string>` parameter is inhabited exactly and is NOT a crossing, while a delegate RETURN, a type argument
// and an array element are. A second copy of that walk lived here and said the opposite about delegate parameters,
// which refused programs Kotlin runs.
//
// Runs on the LOWERED tree, where `memberSig`/`ret` are the final CLR signature: earlier the same node may still be
// mid-resolution and a Kotlin-vocabulary `Nullable(Tv)` would be read as a foreign declaration it is not.
static class ForeignNullableGenericCrossing
{
    public static void Check(JsonNode root, string file)
    {
        Walk(root, file);
    }

    static void Walk(JsonNode node, string file)
    {
        switch (node)
        {
            case JsonObject obj:
                // THE STAMPED DECLARATION IS THE TRIGGER, not a list of node kinds. `memberSig`/`memberRet` exist on
                // exactly the nodes ClrMemberResolution resolved against a .NET member — including an accessor-backed
                // external `field`, whose KIND is Kotlin's too — so keying on them is keyed on the fact itself and
                // cannot drift from where the stamping happens.
                if (obj["memberSig"] != null || obj[ClrMemberResolution.MemberRetKey] != null) CheckCall(obj, file);
                // `memberRet` is a pass-to-pass fact and must not reach CIR: the emitter consumes `memberSig` and
                // knows nothing of this one.
                obj.Remove(ClrMemberResolution.MemberRetKey);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, file);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) Walk(it, file);
                break;
        }
    }


    static void CheckCall(JsonObject call, string file)
    {
        // A call names its member in `method`; a property/field access names it in `name`; a `newClr` names none.
        var member = Str(call["method"]) ?? Str(call["name"]) ?? ".ctor";
        // The owner key differs by node: a call and a property access name it in `type`, a bound method reference in
        // `clrType`, an accessor-backed field in `ownerType`. The message must name the member the author wrote.
        var owner = (TypeJson.Read(call["type"]) ?? TypeJson.Read(call["clrType"]) ?? TypeJson.Read(call["ownerType"]))
            is TypeNode.Fqn f ? f.Name : "<unknown>";
        if (call["memberSig"] is JsonArray sig)
            for (var i = 0; i < sig.Count; i++)
                if (TypeJson.Read(sig[i]) is TypeNode p && NullableGenericErasure.ErasureWouldMove(p))
                    throw Refuse(file, owner, member, "parameter " + i, p);
        // The RETURN is read off the stamped FOREIGN declaration, never off the node's own `ret`: that one is the
        // caller's Kotlin view and has already been erased as a Kotlin slot, so it says `List<object>` for a member
        // declaring `List<int?>` and the crossing would be invisible.
        if (TypeJson.Read(call[ClrMemberResolution.MemberRetKey]) is TypeNode ret
            && NullableGenericErasure.ErasureWouldMove(ret))
            throw Refuse(file, owner, member, "return", ret);
    }

    // WHAT THE MESSAGE MAY OFFER is only what actually works. Constructing the .NET type by hand does NOT: a Kotlin
    // `System.Collections.Generic.List<Int?>()` erases its own argument the same way and builds a `List<object>`, so
    // there is no expression in the language whose physical type is `List<Nullable<Int32>>`. Naming that as a remedy
    // sends the author around a loop that ends where it started, so the refusal names the two things that do move:
    // a different .NET surface, or keeping the value on the .NET side of the boundary.
    static InvalidOperationException Refuse(string file, string owner, string member, string slot, TypeNode t)
        => new(
            $"bir2cir: {file}: the .NET member '{owner}.{member}' declares '{Render(t)}' at its {slot}, which NO "
            + "Kotlin expression inhabits. A nullable value type inside a generic argument, an array element or a "
            + "delegate return is System.Object in Kotlin (#86), so a Kotlin 'List<Int?>' is an "
            + "IReadOnlyList<object> and is not a List<Nullable<Int32>> — unrelated invariant reified generics that "
            + "no conversion relates, and constructing the .NET type from Kotlin erases its argument the same way. "
            + "Change the .NET surface (an overload whose argument is object-typed, or whose element is not a "
            + "nullable value type), or build and pass the value entirely on the .NET side.");


    static string Render(TypeNode t) => t switch
    {
        TypeNode.Nullable n => "System.Nullable<" + Render(n.Of) + ">",
        TypeNode.Oblivious o => Render(o.Of),
        TypeNode.Array a => Render(a.Elem) + "[]",
        TypeNode.ByRef b => "ref " + Render(b.Of),
        TypeNode.Fqn { Args: { } args } fa => fa.Name + "<" + string.Join(", ", args.Select(Render)) + ">",
        TypeNode.Fqn f => f.Name,
        TypeNode.Fn fn => "(" + string.Join(", ", fn.Params.Select(Render)) + ") -> " + Render(fn.Ret),
        _ => t.ToString(),
    };

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
