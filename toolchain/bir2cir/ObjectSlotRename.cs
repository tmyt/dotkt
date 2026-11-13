using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// OBJECT-SLOT RENAME (#73 M5): restore the System.Object BCL slot spellings that kotc stopped emitting. kotc is now
// .NET-agnostic about the three universal `kotlin.Any` methods — it emits the KOTLIN names (`toString`/`hashCode`/
// `equals`) plus pure-Kotlin FACTS: `objectOverride:true` on a method DECLARATION that overrides one of them, and
// `anySlot:true` on a CALL node (callInstance/callStatic/newBoundClrDelegate/newBoundDelegate) whose callee is such an
// override. bir2cir maps the Kotlin name -> the CLR Object slot: toString->ToString, hashCode->GetHashCode,
// equals->Equals. (ilemit's EmitObjMethod + objectOverride slot-reuse both key on the BCL spelling, so the FINAL CIR
// must carry it in both the decl `name` and the objMethod/call `method`.)
//
// Runs at representation entry: before source helper recognition in Program, and through MaterializedBirPayload for
// opaque inline/default BIR before that payload is rewritten or inserted. It is UNCONDITIONAL (ref + rt + app). The ref.dll's
// declarations (kotlin.Any itself, String.toString, every stdlib Any override) and the emitted-name-keyed member index
// must use one canonical BCL slot spelling in every build; skipping the ref build would make downstream binders index
// a different contract. Placing it first means every subsequent pass (FaithfulHintRecognition's collection-`ToString`
// recognition, EnumMemberBinding, MemberStrip's bound-stub match, NetInteropBinding, DeclarationRename, ilemit) sees
// that canonical physical spelling.
static partial class ObjectSlotRename
{
    static readonly Dictionary<string, string> Slot = new(StringComparer.Ordinal)
    {
        ["toString"] = "ToString",
        ["hashCode"] = "GetHashCode",
        ["equals"] = "Equals",
    };

    public static void Apply(JsonNode root)
    {
        Walk(root);
        // Resolve semantic equality before generic frame consumers inspect the
        // apparent collection owner of an inherited Any slot. Payload entry uses
        // this same normalization, including for inline/default bodies.
        KotlinEqualityLowering.Apply(root, preserveSuperIntent: true);
    }

    public static BirGraphValue Apply(BirGraphValue root)
    {
        Rename(root);
        return KotlinEqualityLowering.Apply(root, preserveSuperIntent: true);
    }

    static void Rename(BirGraphValue node)
    {
        if (node is IBirExecutablePayload) return;
        void Call(string kind, OptionalField<string> method, Action<OptionalField<string>> writeMethod,
            Action clearIdentity, Action clearAnySlot, string owner = null, bool? virtualCall = null, bool superCall = false)
        {
            var renamed = RenameField(method);
            writeMethod(renamed);
            clearIdentity?.Invoke();
            if (!RetainAnySlot(kind, renamed.OrDefault(),
                AnySlotRebind.IsVirtualToString(kind, renamed.OrDefault(), true, virtualCall, superCall), owner))
                clearAnySlot();
        }
        switch (node)
        {
            case BirCallInstance value when value.AnySlot.OrDefault():
                Call(value.Tag, value.Method, next => value.Method = next,
                    () => value.DeclarationId = default, () => value.AnySlot = default,
                    (value.OwnerType.OrDefault() as TypeNode.Fqn)?.Name,
                    value.Virtual.HasValue ? value.Virtual.Value : null, value.Super.OrDefault());
                break;
            case BirCallStatic value when value.AnySlot.OrDefault():
                Call(value.Tag, value.Method, next => value.Method = next,
                    () => value.DeclarationId = default, () => value.AnySlot = default);
                break;
            case BirNewBoundDelegate value when value.AnySlot.OrDefault():
                Call(value.Tag, value.Method, next => value.Method = next,
                    () => value.DeclarationId = default, () => value.AnySlot = default);
                break;
            case BirObjectMethod value:
                if (value.AnySlot.OrDefault())
                    Call(value.Tag, value.Method, next => value.Method = next, null, () => value.AnySlot = default);
                else value.Method = RenameField(value.Method);
                break;
            case BirFunctionDeclaration value when value.ObjectOverride.OrDefault():
                value.Name = RenameField(value.Name);
                break;
        }
        node.VisitChildren(Rename);
    }

    static bool RetainAnySlot(string kind, string method, bool virtualToString, string owner) =>
        method is "Equals" or "GetHashCode" || virtualToString
        || kind == "callInstance" && AliasHelperHoist.UsesNativeArrayObjectSlot(owner, true);

    static void Walk(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            // A CALL carrying the `anySlot` fact (its callee is a kotlin.Any override) — rename `method`.
            // Keyed on the flag, NOT the node kind, so it covers callInstance/callStatic AND the (M4.4) bound-delegate
            // nodes uniformly and survives any node-kind change.
            if ((obj["anySlot"] as JsonValue)?.GetValue<bool>() == true)
            {
                RenameField(obj, "method");
                // This semantic override now names the CLR Object slot, not the
                // reference-surface declaration (whose owner can have a different
                // representation). Its source MethodDef identity is consumed here.
                obj.Remove(DeclarationIdentityBinding.Key);
                // Equality and hashing keep their semantic facts until KotlinEqualityLowering consumes them.
                // Virtual ToString calls retain the fact until enum/reference dispatch is bound; native
                // array calls need it until member substitution. Reference-build bodies are squashed.
                if (!RetainAnySlot((obj["k"] as JsonValue)?.GetValue<string>(), obj["method"]?.GetValue<string>(),
                    AnySlotRebind.IsVirtualToString(obj), TypeJson.OwnerName(obj["ownerType"])))
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
        if ((obj[key] as JsonValue)?.GetValue<string>() is string cur)
            obj[key] = RenameName(cur);
    }

    static string RenameName(string name) => name != null && Slot.TryGetValue(name, out var slot) ? slot : name;
    static OptionalField<string> RenameField(OptionalField<string> value) =>
        value.HasValue ? RenameName(value.Value) : value;
}
