using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

// OBJECT-SLOT RENAME (#73 M5): restore the System.Object BCL slot spellings that kotc stopped emitting. kotc is now
// .NET-agnostic about the three universal `kotlin.Any` methods — it emits the KOTLIN names (`toString`/`hashCode`/
// `equals`) plus pure-Kotlin FACTS: `objectOverride:true` on a method DECLARATION that overrides one of them, and
// `anySlot:true` on a CALL node (callInstance/callStatic/newBoundClrDelegate/newBoundDelegate) whose callee is such an
// override. bir2cir maps the Kotlin name -> the CLR Object slot: toString->ToString, hashCode->GetHashCode,
// equals->Equals. (ilemit's EmitObjMethod + objectOverride slot-reuse both key on the BCL spelling, so the FINAL CIR
// must carry it in both the decl `name` and the objMethod/call `method`.)
//
// Runs FIRST in the per-file loop (before every other pass) and UNCONDITIONALLY (ref + rt + app): kotc's former
// `objectMethodName` rename was unconditional, so the ref.dll's decl names (kotlin.Any itself, String.toString, every
// stdlib Any-override) and the emitted-name-keyed member index must stay byte-identical — a ref-build skip would shift
// every downstream binder. Placing it first means every subsequent pass (FaithfulHintRecognition's collection-`ToString`
// recognition, EnumMemberBinding, MemberStrip's bound-stub match, NetInteropBinding, DeclarationRename, ilemit) sees the
// exact same BCL-spelled trees it saw when kotc baked the names — zero downstream changes.
static class ObjectSlotRename
{
    static readonly Dictionary<string, string> Slot = new(StringComparer.Ordinal)
    {
        ["toString"] = "ToString",
        ["hashCode"] = "GetHashCode",
        ["equals"] = "Equals",
    };

    public static void Apply(JsonNode root) => Walk(root);

    static void Walk(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            // A CALL carrying the `anySlot` fact (its callee is a kotlin.Any override) — rename `method`, strip the flag.
            // Keyed on the flag, NOT the node kind, so it covers callInstance/callStatic AND the (M4.4) bound-delegate
            // nodes uniformly and survives any node-kind change.
            if ((obj["anySlot"] as JsonValue)?.GetValue<bool>() == true)
            {
                RenameField(obj, "method");
                obj.Remove("anySlot");
            }
            // An `objMethod` node is UNAMBIGUOUSLY a System.Object virtual call — rename its `method` by bare name.
            else if ((obj["k"] as JsonValue)?.GetValue<string>() == "objMethod")
                RenameField(obj, "method");

            // A method DECLARATION overriding a kotlin.Any universal method — rename its `name` slot so the emitted
            // method reuses the correct System.Object virtual (ilemit binds the override by name via objectOverride).
            if ((obj["objectOverride"] as JsonValue)?.GetValue<bool>() == true)
                RenameField(obj, "name");

            foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value);
        }
        else if (node is JsonArray arr)
            foreach (var it in arr) if (it != null) Walk(it);
    }

    // Map a Kotlin Object-method name at `key` to its BCL slot. A value not in the map (an already-BCL spelling from a
    // bir2cir-internal producer, or any other member) is left untouched — idempotent and safe.
    static void RenameField(JsonObject obj, string key)
    {
        if ((obj[key] as JsonValue)?.GetValue<string>() is string cur && Slot.TryGetValue(cur, out var slot))
            obj[key] = slot;
    }
}
