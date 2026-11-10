using System.Linq;
using System.Text.Json.Nodes;

// APPLIED-ATTRIBUTE EXTERNAL NORMALIZE — mint the ilemit-facing `attrExternal` bool for an applied attribute (#48).
// The `attr` type is a structured `{t:fqn}` identity node; ilemit's BuildCab/StampMemberAttrs/DefineParamNames stamp
// an applied attribute ONLY when its type is emitted in THIS assembly (`_types`) or is flagged EXTERNAL
// (`attrExternal:true`, resolved from a referenced .NET assembly); a merely-referenced type is skipped.
//
// kotc flags imported CLR attributes with their frontend origin fact:
//   (1) kotc flags an IMPORTED .NET attribute (a reference-KLIB-projected annotation class, #54) with `"attrClr":true` — the
//       frontend origin fact (kotc knows the type came from a CLR reference). Consume it: drop `attrClr` +
//       set `attrExternal:true`
//       in EVERY build (the type lives in the BCL / a referenced assembly, resolvable from refs anywhere). This is the
//       CLR-relation decision bir2cir owns; ilemit just reads the flag. (Ex-`clr:` string prefix, retired #48.)
static class AttrExternalNormalize
{
    public static void Apply(JsonNode root) => Walk(root);

    static void Walk(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList()) if (kv.Value != null) Walk(kv.Value);
            if (obj.ContainsKey("attr") && TypeJson.OwnerName(obj["attr"]) is string s)
            {
                if ((obj["attrClr"] as JsonValue)?.TryGetValue<bool>(out var isClr) == true && isClr)
                {
                    obj.Remove("attrClr");                   // (1) imported .NET attr -> external flag (consume kotc's origin fact)
                    obj["attrExternal"] = true;
                }
            }
        }
        else if (node is JsonArray arr) foreach (var it in arr.ToList()) if (it != null) Walk(it);
    }
}
