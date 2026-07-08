using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

// #52 (kotc-purity): SYNTHESIZE the remaining fixed-shape CLR-representation synthetic TYPES here, in the Kotlin<->CLR
// layer, instead of in the kotc frontend. kotc emits only the FACTS (a use-site reference / a `refTypes` registry);
// this pass assembles the actual TYPE definitions and injects them into the file `types`. Three producers move:
//
//   • <>dotkt_CharSequence  — the monomorphic interface (get_length/get/subSequence) a `class S : CharSequence` or a
//     CharSequence-typed slot needs (kotlin.CharSequence has no faithful .NET supertype). Emitted into any file that
//     REFERENCES the identity, mirroring kotc's old per-file `usesCharSeq` trigger (ilemit dedups per assembly and
//     canonicalizes to the rt stdlib's copy when it resolves externally).
//   • <>dotkt_KProperty(+Impl) — the minimal KProperty reflection stub (`name`) a `::prop` / delegated-property use
//     lowers to (a pure binding, #57). Both defs are emitted together into any file that references either identity.
//   • <>dotkt_<scope>_Ref_<elem> — the monomorphized heap cell `class …{ var v }` promoting a captured-and-mutated
//     local. Assembled from the file's `refTypes` registry ({name, element-type}); the element type is unrecoverable
//     from the use-site `field .v` nodes alone, so kotc carries it as the registry fact.
//
// Runs in the Phase-1 per-file loop, AFTER ClosureSynthesis (a closure's invoke body may reference KProperty, so its
// class must already be in `types` to be scanned) and before type lowering. Unconditional (ref/rt/app): kotc emits
// these facts in every build, exactly as its old charSeqIfaceDefs/kPropertyDefs/refDefs ran regardless of build.
static class SharedSyntheticSynthesis
{
    const string CharSeq = "<>dotkt_CharSequence";
    const string KProp = "<>dotkt_KProperty";
    const string KPropImpl = "<>dotkt_KPropertyImpl";

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    static JsonObject Fqn(string name) => new() { ["t"] = "fqn", ["name"] = name };

    public static void Apply(JsonNode root)
    {
        if (root is not JsonObject file) return;
        var types = file["types"] as JsonArray;
        if (types == null) { types = new JsonArray(); file["types"] = types; }
        var present = new HashSet<string>(types.OfType<JsonObject>().Select(t => Str(t["name"])).Where(n => n != null));

        // 1) Heap ref-cells from the registry. Consume + drop `refTypes` (a transient BIR fact, not a CIR field).
        if (file["refTypes"] is JsonArray refTypes)
        {
            foreach (var e in refTypes)
                if (e is JsonObject eo && Str(eo["name"]) is string name && eo["elem"] is JsonNode elem && present.Add(name))
                    types.Add(BuildRefCell(name, elem));
            file.Remove("refTypes");
        }

        // 2) Reference-triggered fixed-shape synthetics. Scan the file (methods + fields + types, including the closure
        // classes ClosureSynthesis just added) for each identity, then inject the matching def once.
        var referenced = new HashSet<string>();
        CollectRefs(file["methods"], referenced);
        CollectRefs(file["fields"], referenced);
        CollectRefs(types, referenced);

        if (referenced.Contains(CharSeq) && present.Add(CharSeq))
            types.Add(JsonNode.Parse(CharSeqDef));
        // KProperty + Impl travel together (Impl implements the interface; a delegated-property call passes an Impl into
        // the rt's getValue(…, <>dotkt_KProperty)). Emit both whenever either identity is referenced.
        if ((referenced.Contains(KProp) || referenced.Contains(KPropImpl)))
        {
            if (present.Add(KProp)) types.Add(JsonNode.Parse(KPropertyDef));
            if (present.Add(KPropImpl)) types.Add(JsonNode.Parse(KPropertyImplDef));
        }
    }

    // Recursively record any string value equal to one of the tracked synthetic names (a type node's `name`, an
    // `ownerType` name, a base/interface entry — every reference surfaces as such a string).
    static void CollectRefs(JsonNode node, HashSet<string> acc)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var kv in o) CollectRefs(kv.Value, acc);
                break;
            case JsonArray a:
                foreach (var it in a) CollectRefs(it, acc);
                break;
            case JsonValue v when v.TryGetValue<string>(out var s):
                if (s == CharSeq || s == KProp || s == KPropImpl) acc.Add(s);
                break;
        }
    }

    // A monomorphized heap cell `class <name>(var v: elem)` — a single field + its init ctor, non-generic (the element
    // type is baked in). Byte-identical to kotc's old refDefs() output.
    static JsonObject BuildRefCell(string name, JsonNode elem)
    {
        var ctorBody = new JsonArray
        {
            new JsonObject
            {
                ["k"] = "setField",
                ["ownerType"] = Fqn(name),
                ["recv"] = new JsonObject { ["k"] = "this" },
                ["name"] = "v",
                ["value"] = new JsonObject { ["k"] = "local", ["name"] = "v" },
            }
        };
        return new JsonObject
        {
            ["name"] = name,
            ["kind"] = "class",
            ["abstract"] = false,
            ["vis"] = "public",
            ["typeParams"] = new JsonArray(),
            ["base"] = null,
            ["interfaces"] = new JsonArray(),
            ["fields"] = new JsonArray { new JsonObject { ["name"] = "v", ["type"] = elem.DeepClone() } },
            ["ctors"] = new JsonArray
            {
                new JsonObject
                {
                    ["params"] = new JsonArray { new JsonObject { ["name"] = "v", ["type"] = elem.DeepClone() } },
                    ["baseArgs"] = null,
                    ["thisArgs"] = null,
                    ["vis"] = "public",
                    ["body"] = ctorBody,
                }
            },
            ["methods"] = new JsonArray(),
        };
    }

    // Fixed-shape defs transcribed verbatim from kotc's retired charSeqIfaceDefs() / kPropertyDefs().
    const string CharSeqDef = """
    {"name":"<>dotkt_CharSequence","kind":"interface","base":null,"fields":[],"ctors":[],"methods":[
      {"name":"get_length","static":false,"override":false,"virtual":true,"objectOverride":false,"vis":"public","params":[],"ret":{"t":"fqn","name":"kotlin.Int"},"body":[]},
      {"name":"get","static":false,"override":false,"virtual":true,"objectOverride":false,"vis":"public","params":[{"name":"index","type":{"t":"fqn","name":"kotlin.Int"}}],"ret":{"t":"fqn","name":"kotlin.Char"},"body":[]},
      {"name":"subSequence","static":false,"override":false,"virtual":true,"objectOverride":false,"vis":"public","params":[{"name":"startIndex","type":{"t":"fqn","name":"kotlin.Int"}},{"name":"endIndex","type":{"t":"fqn","name":"kotlin.Int"}}],"ret":{"t":"fqn","name":"<>dotkt_CharSequence"},"body":[]}
    ]}
    """;

    const string KPropertyDef = """
    {"name":"<>dotkt_KProperty","kind":"interface","base":null,"fields":[],"ctors":[],"methods":[
      {"name":"get_name","static":false,"override":false,"virtual":false,"objectOverride":false,"vis":"public","params":[],"ret":{"t":"fqn","name":"kotlin.String"},"body":[]}
    ]}
    """;

    const string KPropertyImplDef = """
    {"name":"<>dotkt_KPropertyImpl","kind":"class","vis":"public","base":null,"interfaces":[{"t":"fqn","name":"<>dotkt_KProperty"}],"fields":[{"name":"name","type":{"t":"fqn","name":"kotlin.String"}}],"ctors":[{"params":[{"name":"name","type":{"t":"fqn","name":"kotlin.String"}}],"baseArgs":null,"thisArgs":null,"vis":"public","body":[{"k":"setField","ownerType":{"t":"fqn","name":"<>dotkt_KPropertyImpl"},"recv":{"k":"this"},"name":"name","value":{"k":"local","name":"name"}}]}],"methods":[
      {"name":"get_name","static":false,"override":false,"virtual":true,"objectOverride":false,"vis":"public","params":[],"ret":{"t":"fqn","name":"kotlin.String"},"body":[{"k":"return","value":{"k":"field","ownerType":{"t":"fqn","name":"<>dotkt_KPropertyImpl"},"recv":{"k":"this"},"name":"name"}}]}
    ]}
    """;
}
