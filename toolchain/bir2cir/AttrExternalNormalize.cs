using System.Linq;
using System.Text.Json.Nodes;

// APPLIED-ATTRIBUTE EXTERNAL NORMALIZE — mint the ilemit-facing `attrExternal` bool for an applied attribute (#48).
// The `attr` type is a structured `{t:fqn}` identity node; ilemit's BuildCab/StampMemberAttrs/DefineParamNames stamp
// an applied attribute ONLY when its type is emitted in THIS assembly (`_types`) or is flagged EXTERNAL
// (`attrExternal:true`, resolved from a referenced .NET assembly); a merely-referenced type is skipped.
//
// kotc flags an imported .NET attribute (a reference-KLIB-projected annotation class) with `"attrClr":true`.
// Consume that frontend origin fact in every build: drop `attrClr` and set `attrExternal:true`. The type lives
// in the BCL or another referenced assembly. bir2cir owns this CLR-relation decision; ilemit just reads the flag.
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
                    obj.Remove("attrClr");                   // consume kotc's origin fact
                    obj["attrExternal"] = true;
                }
            }
        }
        else if (node is JsonArray arr) foreach (var it in arr.ToList()) if (it != null) Walk(it);
    }
}
