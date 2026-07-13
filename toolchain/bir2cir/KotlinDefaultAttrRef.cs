using System.Linq;
using System.Text.Json.Nodes;

// #146 — CROSS-MODULE @KotlinDefault ATTRIBUTE OWNER RE-POINT (APP builds only).
//
// kotc stamps `[kotlin.clr.KotlinDefault(index, bir)]` on every non-const defaulted parameter of a qualifying
// top-level/extension function (the BIR sub-tree DefaultArgSplice fills at an omitted call site). The attribute CLASS
// `kotlin.clr.KotlinDefault` is a real stdlib annotation (libraries/stdlib/clr/kotlin/clr/ClrIntrinsic.kt): a USER /
// round-trip library only REFERENCES it. ilemit's BuildCab stamps an applied attribute ONLY when its type is emitted
// in THIS assembly (`_types`) or is a `clr:`-imported .NET type — a merely-referenced stdlib attribute is SKIPPED
// ("type not emitted in this assembly"), so the library .dll would carry NO [KotlinDefault] and a consumer's
// DefaultArgSplice would have nothing to read (the non-const cross-module default would silently not fill).
//
// Fix: in the APP build, re-point the applied attr owner from the bare FQN `kotlin.clr.KotlinDefault` to its
// `clr:`-imported form, so ilemit's clr: path resolves the type from the referenced stdlib (rt.dll) and stamps it.
// The STDLIB self-build (ref/rt) is UNTOUCHED: it DEFINES the type in `_types`, so the bare-FQN local-stamp path is
// correct there (a clr: re-point would try to resolve the type from refs where it does not yet exist). App-only, so
// the stdlib self-builds stay byte-identical. Consumers (facadegen / bir2cir ReferenceMetadataIndex) read the attr
// by its FullName `kotlin.clr.KotlinDefault`, which the clr:-imported reference preserves.
static class KotlinDefaultAttrRef
{
    const string Fqn = "kotlin.clr.KotlinDefault";

    public static void Apply(JsonNode root) => Walk(root);

    static void Walk(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList()) if (kv.Value != null) Walk(kv.Value);
            if (obj["attr"] is JsonValue v && v.TryGetValue<string>(out var s) && s == Fqn)
                obj["attr"] = "clr:" + Fqn;
        }
        else if (node is JsonArray arr) foreach (var it in arr.ToList()) if (it != null) Walk(it);
    }
}
