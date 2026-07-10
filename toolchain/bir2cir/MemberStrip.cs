using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// MEMBER-STRIP (clrName migration) — the member-level mirror of the @ClrTypeAlias type-strip. Once kotc stops reading
// @ClrIntrinsic it can no longer exclude a bound-stub declaration (the `clrName(it)==null` filters in BirEmitter), so
// those @ClrIntrinsic-bound members/top-level funs get EMITTED (with throwing TODO bodies). This pass DROPS them: the
// call sites are substituted to the BCL member by MemberCallSubstitution, so the stub itself must not survive. Matched
// by FULL SIGNATURE (name + canonical param types) so StringBuilder.append(Char)@ClrIntrinsic is dropped while
// append(CharSequence?) (rule-3, real body) is kept. For an ALIAS-class owner a member that merely OVERRIDES a
// @ClrIntrinsic member is ALSO a bound stub (its call substitutes to the BCL), so it is dropped too (else it over-hoists
// into the rule-3 helper). Runs BEFORE AliasHelperHoist. Never in ref.
static class MemberStrip
{
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        if (root is not JsonObject obj) return;
        if ((obj["fileClass"] as JsonValue)?.GetValue<string>() is string fc && obj["methods"] is JsonArray rootMethods)
            StripFrom(rootMethods, fc, refs, null, false);
        if (obj["types"] is not JsonArray types) return;
        foreach (var t in types)
            if (t is JsonObject td && (td["name"] as JsonValue)?.GetValue<string>() is string owner)
            {
                // NEVER strip an INTERFACE's members: a non-alias interface (EnumEntries, MatchGroupCollection) declares
                // the CLR slot (renamed get_Item/get_Count) that implementers bind to — it is not a throwing bound stub.
                // (A @ClrTypeAlias interface is dropped whole by AliasHelperHoist anyway.)
                if ((td["kind"] as JsonValue)?.GetValue<string>() == "interface") continue;
                var stripped = new HashSet<string>(StringComparer.Ordinal);
                var isAlias = ReferenceMetadataIndex.BareOwnerFqn(owner) is string bo && refs.Aliases.ContainsKey(bo);
                if (td["methods"] is JsonArray methods) StripFrom(methods, owner, refs, stripped, isAlias);
                if (td["properties"] is JsonArray props && stripped.Count > 0) DropDanglingProps(props, stripped);
            }
    }

    static void StripFrom(JsonArray methods, string owner, ReferenceMetadataIndex refs, HashSet<string> stripped, bool alias)
    {
        for (var i = methods.Count - 1; i >= 0; i--)
        {
            if (methods[i] is not JsonObject mo) continue;
            if ((mo["name"] as JsonValue)?.GetValue<string>() is not string name) continue;
            var keys = (mo["params"] as JsonArray ?? new JsonArray())
                .Select(p => ReferenceMetadataIndex.ParamKey((p as JsonObject)?["type"])).ToList();
            // An alias-class member that overrides a @ClrIntrinsic ancestor is normally a bound stub (its call
            // substitutes to the BCL), so it is dropped. But a GENUINE rule-3 member — concrete + intrinsic-less in
            // the ref.dll (String.compareTo's ordinal body overriding the culture-sensitive Comparable.compareTo@ClrIntrinsic)
            // — carries a REAL Kotlin body that must be PRESERVED and hoisted (else the call would resolve to the
            // semantically-wrong BCL slot). IsRule3Member is exactly that ref.dll signal, so exempt it from the override-drop.
            var drop = refs.IsBoundStub(owner, name, keys)
                || (alias && mo["overrides"] is JsonArray ovs && DeclarationRename.ResolveSlot(ovs, refs) != null
                    && !refs.IsRule3Member(owner, name));
            if (drop) { stripped?.Add(name); methods.RemoveAt(i); }
        }
    }

    // A property record whose accessor method was stripped (a bound-stub property) is itself bound — drop the record.
    static void DropDanglingProps(JsonArray props, HashSet<string> stripped)
    {
        for (var i = props.Count - 1; i >= 0; i--)
            if (props[i] is JsonObject po
                && (((po["get"] as JsonValue)?.GetValue<string>() is string g && stripped.Contains(g))
                 || ((po["set"] as JsonValue)?.GetValue<string>() is string s && stripped.Contains(s))))
                props.RemoveAt(i);
    }
}

