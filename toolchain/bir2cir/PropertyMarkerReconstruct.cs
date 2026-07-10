using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

// PROPERTY-MARKER RECONSTRUCT (#78/#81 — REF build only). kotc emits a companion/top-level property-accessor CALL as
// the property's BARE Kotlin identity + a `"prop":"get"/"set"` accessor-KIND marker (it no longer bakes the
// `get_`/`set_` slot name at the call site). In an APP/RT build MemberCallSubstitution consumes that marker — trying a
// @ClrProperty/@ClrIntrinsic binding on the bare name for a CLR-bound owner, else reconstructing kotc's OWN
// `get_`/`set_<name>` declaration-side accessor convention (Program.cs TransformCall). The REF build SKIPS
// MemberCallSubstitution entirely (the reference surface is pure Kotlin — there is nothing to bind), so a leftover
// marker would reach ilemit as a bare property name and fail to resolve (e.g. `IntrinsicsKt.COROUTINE_SUSPENDED not
// found` for the top-level computed `val COROUTINE_SUSPENDED`). This pass performs the identical pure-syntactic
// reconstruction for the ref build — the marker is not BIR/CIR vocabulary, so it is stripped either way.
static class PropertyMarkerReconstruct
{
    public static JsonNode Apply(JsonNode root) { Walk(root); return root; }

    static void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                // Only the STATIC property axis carries the marker (a `callStatic`); indexer markers
                // (`index-get`/`index-set`) are a .NET-interop concern absent from the ref self-build. All four #81
                // sites are `"owner"`-keyed (SITE A/C an FQN, SITE B/C7 `null`); require the `owner` key so a future
                // `ownerType`-keyed interop static-property node (NetInteropBinding's domain, app-only) is NOT mangled
                // here — it would be unresolvable in ref mode anyway, so it must fail with its own shape.
                if ((o["k"] as JsonValue)?.GetValue<string>() == "callStatic" && o.ContainsKey("owner")
                    && (o["prop"] as JsonValue)?.GetValue<string>() is ("get" or "set") and var kind
                    && (o["method"] as JsonValue)?.GetValue<string>() is string m)
                {
                    o["method"] = (kind == "set" ? "set_" : "get_") + m;
                    o.Remove("prop");
                }
                foreach (var key in o.Select(kv => kv.Key).ToList())
                    if (o[key] is JsonNode child) Walk(child);
                break;
            case JsonArray a:
                foreach (var c in a) if (c is JsonNode cn) Walk(cn);
                break;
        }
    }
}
