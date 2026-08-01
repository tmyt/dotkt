using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        CheckImplementedSlots(root, file);
        Walk(root, file);
    }

    // THE SAME CROSSING AT THE IMPLEMENTING POSITION. A call is not the only way to meet an uninhabitable slot: a
    // Kotlin class can DERIVE from a .NET type that declares one — `class C : ITake` for a C# `interface ITake {
    // string Take(List<int?> xs); }` — and there the crossing is in the slot the class must fill, not in anything it
    // calls. Nothing above sees it, because no node resolves against a member; the class compiled clean and died at
    // load with "Signature of the body and declaration in a method implementation do not match", or, for the
    // abstract base twin, "does not have an implementation".
    //
    // FILLING THE SLOT FROM THE REFLECTED DECLARATION IS NOT A FIX, though the reflected signature is right there to
    // copy. A method emitted with the declaration's own `List<Nullable<int32>>` parameter would still have a Kotlin
    // BODY, and that body reads its parameter as the `List<object>` Kotlin says it is — the identical pair of
    // unrelated invariant reified generics the call-side refusal exists to prevent, except silent rather than
    // load-time. There is no Kotlin expression that inhabits the parameter type, so the body cannot legitimately use
    // its own argument: the override has no valid CIL lowering, and the author is owed the message rather than a
    // TypeLoadException with our type's name on it.
    //
    // ASKED OF EVERY PROVENANCE. The carrier machinery that repairs erased slots reads DotKt metadata and so covers
    // only DotKt-authored supertypes; a plain BCL or third-party interface has none, which is exactly the column
    // that fell through. This asks the REFLECTED declaration, which every referenced assembly has.
    static void CheckImplementedSlots(JsonNode root, string file)
    {
        if (root is not JsonObject o || o["types"] is not JsonArray types) return;
        foreach (var t in types) if (t is JsonObject to) CheckTypeSlots(to, file);
    }

    static void CheckTypeSlots(JsonObject to, string file)
    {
        if (to["types"] is JsonArray nested)
            foreach (var n in nested) if (n is JsonObject nto) CheckTypeSlots(nto, file);

        var supers = new List<JsonNode>();
        if (to["base"] is JsonNode b) supers.Add(b);
        if (to["interfaces"] is JsonArray ifs) supers.AddRange(ifs.Where(i => i != null));

        // The names+arities this type declares, for the CONCRETE-slot case: overriding a non-abstract virtual slot
        // whose signature crosses is the same dead end, but only when the type actually overrides it.
        var declared = new HashSet<string>(StringComparer.Ordinal);
        if (to["methods"] is JsonArray ms)
            foreach (var m in ms.OfType<JsonObject>())
                if (Str(m["name"]) is string mn)
                    declared.Add(mn + "/" + ((m["params"] as JsonArray)?.Count ?? 0));

        foreach (var sup in supers)
        {
            if (TypeJson.Read(sup) is not TypeNode.Fqn supFqn) continue;
            var open = ClrMemberResolution.ResolveOwnerType(supFqn);
            if (open == null) continue;   // a LOCAL supertype: emitted here, and erased consistently with its users
            MethodInfo[] slots;
            try
            {
                slots = open.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch (NotSupportedException) { continue; }
            foreach (var slot in slots)
            {
                if (!slot.IsVirtual || slot.IsSpecialName) continue;
                // An ABSTRACT slot must be filled by this type, so its crossing is unavoidable; a CONCRETE virtual
                // one is only a problem where the type actually overrides it.
                if (!slot.IsAbstract && !declared.Contains(slot.Name + "/" + slot.GetParameters().Length)) continue;
                var ps = slot.GetParameters();
                for (var i = 0; i < ps.Length; i++)
                    if (NullableGenericErasure.ErasureWouldMove(ClrMemberResolution.MemberSigOf(ps[i].ParameterType)))
                        throw RefuseSlot(file, Str(to["name"]) ?? "<type>", supFqn.Name, slot.Name, "parameter " + i,
                            ClrMemberResolution.MemberSigOf(ps[i].ParameterType));
                if (slot.ReturnType != typeof(void)
                    && NullableGenericErasure.ErasureWouldMove(ClrMemberResolution.MemberSigOf(slot.ReturnType)))
                    throw RefuseSlot(file, Str(to["name"]) ?? "<type>", supFqn.Name, slot.Name, "return",
                        ClrMemberResolution.MemberSigOf(slot.ReturnType));
            }
        }
    }

    static InvalidOperationException RefuseSlot(string file, string type, string owner, string member, string slot,
        TypeNode t)
        => new(
            $"bir2cir: {file}: '{type}' derives from '{owner}', whose member '{member}' declares '{Render(t)}' at its "
            + $"{slot} — a slot NO Kotlin expression inhabits. A nullable value type inside a generic argument, an "
            + "array element or a delegate return is System.Object in Kotlin (#86), so the Kotlin method filling "
            + "this slot would receive a 'List<object>' where the declaration says 'List<Nullable<Int32>>' — "
            + "unrelated invariant reified generics that no conversion relates. Emitting the declaration's own "
            + "signature would not help: the Kotlin body still reads the argument as the Kotlin type, so the "
            + "mismatch would move from load time into the body. Change the .NET surface (a slot whose argument is "
            + "object-typed, or whose element is not a nullable value type), or implement this interface on the "
            + ".NET side.");


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
