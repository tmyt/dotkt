using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// #52 (kotc-purity): SYNTHESIZE the remaining fixed-shape CLR-representation synthetic TYPES here, in the Kotlin<->CLR
// layer, instead of in the kotc frontend. kotc emits only the FACTS (a use-site reference / a `refTypes` registry);
// this pass assembles the actual TYPE definitions and injects them into the file `types`. Two producers move:
//
//   • dotkt$CharSequence  — the monomorphic interface (get_length/get/subSequence) a `class S : CharSequence` or a
//     CharSequence-typed slot needs (kotlin.CharSequence has no faithful .NET supertype). Emitted into any file that
//     REFERENCES the identity because local uses need a declaration; ilemit dedups that generated identity per assembly
//     and resolves external uses to the runtime stdlib's canonical definition.
//   • the scoped generated ref-cell identity — the heap cell `class …{ var v }` promoting a captured-and-mutated local.
//     Assembled from the file's `refTypes` registry ({name, element-type}); the element type is unrecoverable from the use-site
//     `field .v` nodes alone, so kotc carries it as the registry fact. A closed element stays monomorphic. An element
//     mentioning an enclosing type/method variable becomes a generic cell whose parameters preserve the complete
//     bound closure, and every bare use-site identity becomes the corresponding constructed cell.
//
// (`dotkt$KProperty(+Impl)` — formerly synthesized here too — is RETIRED, #70: `kotlin.reflect.KProperty*` is now a
// REAL emitted stdlib interface, and kotc's `propertyRef`/`kPropertyStub` materialize real implementations of it
// directly via the ordinary `liftedTypes`/`new` machinery, like any other lifted class — no bir2cir synthesis needed.)
//
// Runs in the Phase-1 per-file loop, AFTER ClosureSynthesis (a closure's invoke body may reference CharSequence, so
// its class must already be in `types` to be scanned) and before type lowering. Unconditional (ref/rt/app): these BIR
// facts exist in every build, and every consumer requires the same generated-type identity contract.
static class SharedSyntheticSynthesis
{
    // #68: `dotkt$…` names use Kotlin's OWN unspeakable marker `$` (the string-template char; normal Kotlin source cannot
    // produce it — the frontend-legit analog of C#'s `<>`, NOT CLR knowledge). A SINGLE canonical spelling everywhere
    // (kotc emits it, bir2cir synthesizes it, ilemit emits it verbatim). Every def carries `generated:true`; ilemit reads
    // that flag to stamp [System.Runtime.CompilerServices.CompilerGenerated].
    public const string CharSeq = "dotkt$CharSequence";

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    static JsonObject Fqn(string name) => new() { ["t"] = "fqn", ["name"] = name };

    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        if (root is not JsonObject file) return;
        var types = file["types"] as JsonArray;
        if (types == null) { types = new JsonArray(); file["types"] = types; }
        var present = new HashSet<string>(types.OfType<JsonObject>().Select(t => Str(t["name"])).Where(n => n != null));

        // 1) Heap ref-cells from the registry. Consume + drop `refTypes` (a transient BIR fact, not a CIR field).
        if (file["refTypes"] is JsonArray refTypes)
        {
            var specs = new Dictionary<string, RefCellSpec>(StringComparer.Ordinal);
            foreach (var e in refTypes)
                if (e is JsonObject eo && Str(eo["name"]) is string name && eo["elem"] is JsonNode elem)
                {
                    var spec = new RefCellSpec(name, elem, eo[KotlinSupertypesRecord.PreKey]);
                    spec.Bind(eo["typeParams"].AsArray());
                    specs.Add(name, spec);
                }

            var liveCells = new HashSet<string>(StringComparer.Ordinal);
            void CollectCellUses(JsonNode node)
            {
                if (ReferenceEquals(node, refTypes)) return;
                if (node is JsonObject obj)
                {
                    if (Str(obj["t"]) == "fqn" && Str(obj["name"]) is string name) liveCells.Add(name);
                    foreach (var pair in obj)
                        if (pair.Key != "sharedCellType") CollectCellUses(pair.Value);
                }
                else if (node is JsonArray array) foreach (var child in array) CollectCellUses(child);
            }
            CollectCellUses(file);
            foreach (var spec in specs.Values)
            {
                // Inline carriers author a possible cell without requiring an allocation. A byref-like value
                // used only in place must not acquire an unused, illegal heap TypeDef from that declaration hint.
                if (!liveCells.Contains(spec.Name)
                    && FieldLegality.Classify(TypeJson.Read(spec.Elem), refs.IsByRefLikeFqn, out _) == FieldRejection.ByRefLike)
                    continue;
                if (present.Add(spec.Name))
                {
                    var cell = BuildRefCell(spec);
                    if (spec.KotlinFacts is JsonNode frameFacts)
                        cell[KotlinSupertypesRecord.PreKey] = frameFacts.DeepClone();
                    ClosureSynthesis.RecordCaptureLegality(cell, file, refs);
                    types.Add(cell);
                }
            }
            file.Remove("refTypes");
        }

        // Local-function frame correspondences are transient BIR facts, never part of CIR.
        DropSyntheticTypeArgs(file);

        // 2) Reference-triggered fixed-shape synthetics. Scan the file (methods + fields + types, including the closure
        // classes ClosureSynthesis just added) for each identity, then inject the matching def once.
        var referenced = new HashSet<string>();
        CollectRefs(file["methods"], referenced);
        CollectRefs(file["fields"], referenced);
        CollectRefs(types, referenced);

        if (referenced.Contains(CharSeq) && present.Add(CharSeq))
            types.Add(JsonNode.Parse(CharSeqDef));
    }

    public static void DropSyntheticTypeArgs(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                o.Remove("_syntheticTypeArgs");
                o.Remove("sharedCellType");
                o.Remove("sharedCellTypeParams");
                foreach (var kv in o) if (kv.Value != null) DropSyntheticTypeArgs(kv.Value);
                break;
            case JsonArray a:
                foreach (var item in a) if (item != null) DropSyntheticTypeArgs(item);
                break;
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
                if (s == CharSeq) acc.Add(s);
                break;
        }
    }

    readonly record struct TvKey(string Scope, int Index);

    sealed class RefCellSpec
    {
        public string Name { get; }
        public JsonNode Elem { get; }
        public JsonNode KotlinFacts { get; }
        public List<TvKey> Free { get; } = new();
        public Dictionary<TvKey, JsonNode> Descriptors { get; } = new();

        public RefCellSpec(string name, JsonNode elem, JsonNode kotlinFacts = null)
        {
            Name = name;
            Elem = elem.DeepClone();
            KotlinFacts = kotlinFacts?.DeepClone();
            AddFreeTvs(Elem, Free);
            SortFree();
        }

        public void Bind(JsonArray typeParams)
        {
            // A materialized frame is an explicit declaration/use correspondence; pruning an unused ordinary
            // source slot would invalidate both its companion positions and every already-closed application.
            if (KotlinFacts is JsonValue facts && facts.TryGetValue<string>(out var json)
                && JsonNode.Parse(json)?[NullableRepresentationFrame.MetadataKey] is JsonNode frameNode)
            {
                var frame = NullableRepresentationFrame.Read(frameNode);
                if (frame.PhysicalArity != typeParams.Count)
                    throw new InvalidOperationException("Ref-cell frame does not match its physical parameters");
                for (var i = 0; i < typeParams.Count; i++)
                    if (!Free.Contains(new TvKey("type", i))) Free.Add(new TvKey("type", i));
            }
            // A bound may itself mention another TV (`S : Segment<S>` or `T : Pair<T,U>`). Those variables are part
            // of the generated cell's signature too, even when they do not occur directly in the element type.
            for (var i = 0; i < Free.Count; i++)
            {
                var key = Free[i];
                if (key.Scope != "type" || key.Index < 0 || key.Index >= typeParams.Count
                    || typeParams[key.Index] is not JsonNode descriptor)
                    throw new InvalidOperationException(
                        $"ref-cell `{Name}` cannot resolve declared {key.Scope} type parameter #{key.Index}");

                var normalized = NormalizeDescriptor(descriptor);
                Descriptors.Add(key, normalized);
                if (normalized is JsonObject no && no["constraints"] is JsonArray constraints)
                    foreach (var constraint in constraints)
                        AddFreeTvs(constraint, Free);
            }
            SortFree();
        }

        void SortFree() => Free.Sort((a, b) =>
        {
            var scope = StringComparer.Ordinal.Compare(a.Scope, b.Scope);
            return scope != 0 ? scope : a.Index.CompareTo(b.Index);
        });

        static JsonNode NormalizeDescriptor(JsonNode descriptor)
        {
            if (descriptor is JsonValue)
                return new JsonObject { ["constraints"] = new JsonArray() };
            if (descriptor is not JsonObject o)
                throw new InvalidOperationException("type parameter descriptor must be a name or an object");
            return new JsonObject
            {
                ["constraints"] = o["constraints"]?.DeepClone() ?? new JsonArray(),
            };
        }
    }

    static void AddFreeTvs(JsonNode node, List<TvKey> free)
    {
        switch (node)
        {
            case JsonObject o:
                if (Str(o["t"]) == "tv" && Str(o["scope"]) is string scope
                    && o["i"] is JsonValue iv && iv.TryGetValue<int>(out var index))
                {
                    var key = new TvKey(scope, index);
                    if (!free.Contains(key)) free.Add(key);
                    return;
                }
                foreach (var kv in o)
                    if (kv.Value != null) AddFreeTvs(kv.Value, free);
                break;
            case JsonArray a:
                foreach (var item in a)
                    if (item != null) AddFreeTvs(item, free);
                break;
        }
    }

    static JsonNode RemapTvs(JsonNode node, IReadOnlyDictionary<TvKey, int> positions)
    {
        if (node is JsonObject o && Str(o["t"]) == "tv" && Str(o["scope"]) is string scope
            && o["i"] is JsonValue iv && iv.TryGetValue<int>(out var index)
            && positions.TryGetValue(new TvKey(scope, index), out var position))
            return new JsonObject { ["t"] = "tv", ["scope"] = "type", ["i"] = position };

        var clone = node.DeepClone();
        switch (clone)
        {
            case JsonObject co:
                foreach (var key in co.Select(kv => kv.Key).ToArray())
                    if (co[key] is JsonNode child) co[key] = RemapTvs(child, positions);
                break;
            case JsonArray ca:
                for (var i = 0; i < ca.Count; i++)
                    if (ca[i] is JsonNode child) ca[i] = RemapTvs(child, positions);
                break;
        }
        return clone;
    }

    // A heap cell `class <name><T…>(var v: elem)` — a single field + its init ctor. Closed elements use the canonical
    // monomorphic form; open elements become a constrained generic cell constructed at every lexical use.
    static JsonObject BuildRefCell(RefCellSpec spec)
    {
        var positions = spec.Free.Select((key, index) => (key, index)).ToDictionary(x => x.key, x => x.index);
        var elem = RemapTvs(spec.Elem, positions);
        var typeParams = new JsonArray();
        for (var i = 0; i < spec.Free.Count; i++)
        {
            var descriptor = spec.Descriptors[spec.Free[i]];
            var constraints = descriptor["constraints"] as JsonArray;
            if (constraints == null || constraints.Count == 0)
                typeParams.Add($"T{i}");
            else
                typeParams.Add(new JsonObject
                {
                    ["name"] = $"T{i}",
                    ["constraints"] = new JsonArray(
                        constraints.Select(c => c == null ? null : RemapTvs(c, positions)).ToArray()),
                });
        }

        var self = new JsonObject { ["t"] = "fqn", ["name"] = spec.Name };
        if (spec.Free.Count != 0)
            self["args"] = new JsonArray(Enumerable.Range(0, spec.Free.Count).Select(i => (JsonNode)new JsonObject
            {
                ["t"] = "tv",
                ["scope"] = "type",
                ["i"] = i,
            }).ToArray());

        var ctorBody = new JsonArray
        {
            new JsonObject
            {
                ["k"] = "setField",
                ["ownerType"] = self,
                ["recv"] = new JsonObject { ["k"] = "this" },
                ["name"] = "v",
                ["value"] = new JsonObject { ["k"] = "local", ["name"] = "v" },
            }
        };
        return new JsonObject
        {
            ["name"] = spec.Name,
            ["kind"] = "class",
            ["generated"] = true,
            ["abstract"] = false,
            ["vis"] = "public",
            ["typeParams"] = typeParams,
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

    // Fixed-shape CLR contract for the generated CharSequence bridge interface.
    const string CharSeqDef = """
    {"name":"dotkt$CharSequence","kind":"interface","generated":true,"base":null,"fields":[],"ctors":[],"methods":[
      {"name":"length","propertyName":"length","propertyAccessor":"get","propertyAssociation":"charsequence-length","static":false,"override":false,"virtual":true,"abstract":true,"objectOverride":false,"vis":"public","params":[],"ret":{"t":"fqn","name":"kotlin.Int"},"body":[]},
      {"name":"get","static":false,"override":false,"virtual":true,"abstract":true,"objectOverride":false,"vis":"public","params":[{"name":"index","type":{"t":"fqn","name":"kotlin.Int"}}],"ret":{"t":"fqn","name":"kotlin.Char"},"body":[]},
      {"name":"subSequence","static":false,"override":false,"virtual":true,"abstract":true,"objectOverride":false,"vis":"public","params":[{"name":"startIndex","type":{"t":"fqn","name":"kotlin.Int"}},{"name":"endIndex","type":{"t":"fqn","name":"kotlin.Int"}}],"ret":{"t":"fqn","name":"dotkt$CharSequence"},"body":[]}
    ],"properties":[{"name":"length","type":{"t":"fqn","name":"kotlin.Int"},"kotlinAccessors":["get"],"propertyAssociation":"charsequence-length"}]}
    """;
}
