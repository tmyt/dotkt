using System.Linq;
using System.Text.Json.Nodes;

// APPLIED-ATTRIBUTE EXTERNAL NORMALIZE — resolve the `attr` owner to a plain bare-FQN identity + an `attrExternal`
// bool, retiring the legacy `clr:` attr-owner prefix (#48). ilemit's BuildCab/StampMemberAttrs/DefineParamNames stamp
// an applied attribute ONLY when its type is emitted in THIS assembly (`_types`) or is flagged EXTERNAL
// (`attrExternal:true`, resolved from a referenced .NET assembly); a merely-referenced type is skipped.
//
// Two sources of an external applied attribute:
//   (1) kotc renders an IMPORTED .NET attribute (a facadegen-injected annotation class, #54) as `attr:"clr:System.X"`.
//       Strip the `clr:` prefix -> bare `System.X` + `attrExternal:true` in EVERY build (the type lives in the BCL /
//       a referenced assembly, resolvable from refs anywhere). This replaces ilemit's retired `attr.StartsWith("clr:")`
//       branch — bir2cir owns the CLR-relation decision, ilemit just reads the flag.
//   (2) #146 — a non-const-defaulted param carries `[kotlin.clr.KotlinDefault(index, bir)]` (DefaultArgSplice fills it
//       at an omitted call site). The attribute CLASS is a real stdlib annotation a USER / round-trip library only
//       REFERENCES; in an APP build it is NOT in `_types`, so mark it `attrExternal:true` (ilemit resolves it from the
//       referenced stdlib rt.dll). The STDLIB self-build (ref/rt) DEFINES the type in `_types`, so it must stay LOCAL
//       there (app-only) — the self-builds stay byte-identical.
static class AttrExternalNormalize
{
    const string KotlinDefaultFqn = "kotlin.clr.KotlinDefault";

    public static void Apply(JsonNode root, bool appBuild) => Walk(root, appBuild);

    static void Walk(JsonNode node, bool appBuild)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList()) if (kv.Value != null) Walk(kv.Value, appBuild);
            if (obj["attr"] is JsonValue v && v.TryGetValue<string>(out var s) && s != null)
            {
                if (s.StartsWith("clr:", System.StringComparison.Ordinal))
                {
                    obj["attr"] = s.Substring(4);            // (1) imported .NET attr -> bare FQN + external flag
                    obj["attrExternal"] = true;
                }
                else if (appBuild && s == KotlinDefaultFqn)  // (2) #146 cross-module @KotlinDefault (app-only)
                    obj["attrExternal"] = true;
            }
        }
        else if (node is JsonArray arr) foreach (var it in arr.ToList()) if (it != null) Walk(it, appBuild);
    }
}
