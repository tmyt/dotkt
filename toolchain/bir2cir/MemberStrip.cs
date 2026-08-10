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
                var strippedPropertyAssociations = new HashSet<string>(StringComparer.Ordinal);
                var isAlias = ReferenceMetadataIndex.BareOwnerFqn(owner) is string bo && refs.Aliases.ContainsKey(bo);
                if (td["methods"] is JsonArray methods)
                    StripFrom(methods, owner, refs, strippedPropertyAssociations, isAlias);
                if (td["properties"] is JsonArray props && strippedPropertyAssociations.Count > 0)
                    DropDanglingProps(props, strippedPropertyAssociations);
            }
    }

    static void StripFrom(JsonArray methods, string owner, ReferenceMetadataIndex refs,
        HashSet<string> strippedPropertyAssociations, bool alias)
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
                || (alias && mo["overrides"] is JsonArray ovs
                    && DeclarationRename.ResolveSlot(mo, ovs, refs) != null
                    && !refs.IsRule3Member(owner, name));
            if (drop)
            {
                if (strippedPropertyAssociations != null
                    && KotlinPropertyAccessors.TryIdentity(mo, out _, out _)
                    && Str(mo[KotlinPropertyAccessors.AssociationKey]) is string association)
                    strippedPropertyAssociations.Add(association);
                methods.RemoveAt(i);
            }
        }
    }

    // A property record whose associated accessor was stripped is itself bound. Association, not the temporary
    // legacy MethodDef spelling, identifies it: same-name context/extension overloads may share that spelling here.
    static void DropDanglingProps(JsonArray props, HashSet<string> strippedAssociations)
    {
        for (var i = props.Count - 1; i >= 0; i--)
            if (props[i] is JsonObject po
                && Str(po[KotlinPropertyAccessors.AssociationKey]) is string association
                && strippedAssociations.Contains(association))
                props.RemoveAt(i);
    }

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
