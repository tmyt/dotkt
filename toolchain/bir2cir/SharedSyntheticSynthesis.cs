using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

// #52 (kotc-purity): SYNTHESIZE the remaining fixed-shape CLR-representation synthetic TYPES here, in the Kotlin<->CLR
// layer, instead of in the kotc frontend. kotc emits only the FACTS (a use-site reference / a `refTypes` registry);
// this pass assembles the actual TYPE definitions and injects them into the file `types`. Two producers move:
//
//   • dotkt$CharSequence  — the monomorphic interface (get_length/get/subSequence) a `class S : CharSequence` or a
//     CharSequence-typed slot needs (kotlin.CharSequence has no faithful .NET supertype). Emitted into any file that
//     REFERENCES the identity, mirroring kotc's old per-file `usesCharSeq` trigger (ilemit dedups per assembly and
//     canonicalizes to the rt stdlib's copy when it resolves externally).
//   • dotkt_<scope>_Ref_<elem> — the heap cell `class …{ var v }` promoting a captured-and-mutated local. Assembled
//     from the file's `refTypes` registry ({name, element-type}); the element type is unrecoverable from the use-site
//     `field .v` nodes alone, so kotc carries it as the registry fact. A closed element stays monomorphic. An element
//     mentioning an enclosing type/method variable becomes a generic cell whose parameters preserve the complete
//     bound closure, and every bare use-site identity becomes the corresponding constructed cell.
//
// (`dotkt$KProperty(+Impl)` — formerly synthesized here too — is RETIRED, #70: `kotlin.reflect.KProperty*` is now a
// REAL emitted stdlib interface, and kotc's `propertyRef`/`kPropertyStub` materialize real implementations of it
// directly via the ordinary `liftedTypes`/`new` machinery, like any other lifted class — no bir2cir synthesis needed.)
//
// Runs in the Phase-1 per-file loop, AFTER ClosureSynthesis (a closure's invoke body may reference CharSequence, so
// its class must already be in `types` to be scanned) and before type lowering. Unconditional (ref/rt/app): kotc
// emits these facts in every build, exactly as its old charSeqIfaceDefs/refDefs ran regardless of build.
static class SharedSyntheticSynthesis
{
    // #68: `dotkt$…` names use Kotlin's OWN unspeakable marker `$` (the string-template char; normal Kotlin source cannot
    // produce it — the frontend-legit analog of C#'s `<>`, NOT CLR knowledge). A SINGLE canonical spelling everywhere
    // (kotc emits it, bir2cir synthesizes it, ilemit emits it verbatim). Every def carries `generated:true`; ilemit reads
    // that flag to stamp [System.Runtime.CompilerServices.CompilerGenerated].
    public const string CharSeq = "dotkt$CharSequence";

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
            var specs = new Dictionary<string, RefCellSpec>(StringComparer.Ordinal);
            foreach (var e in refTypes)
                if (e is JsonObject eo && Str(eo["name"]) is string name && eo["elem"] is JsonNode elem)
                    specs.Add(name, new RefCellSpec(name, elem));

            // kotc deliberately emits a bare Kotlin-side synthetic identity at each use. Deciding that a cell whose
            // element mentions an outer TV must itself be generic is CLR representation lowering, so do it here:
            // recover the lexical parameter descriptors, close over TVs mentioned by their bounds, then construct
            // every cell use with the original outer TVs.
            BindRefCellContexts(file, specs);
            foreach (var spec in specs.Values)
                if (spec.Free.Count != 0 && !spec.IsBound)
                    throw new InvalidOperationException(
                        $"generic ref-cell `{spec.Name}` has no lexical use from which to preserve type-parameter constraints");
            RewriteRefCellUses(file, specs);

            foreach (var spec in specs.Values)
                if (present.Add(spec.Name))
                    types.Add(BuildRefCell(spec));
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
        public List<TvKey> Free { get; } = new();
        public Dictionary<TvKey, JsonNode> Descriptors { get; } = new();
        public bool IsBound { get; private set; }

        public RefCellSpec(string name, JsonNode elem)
        {
            Name = name;
            Elem = elem.DeepClone();
            AddFreeTvs(Elem, Free);
            SortFree();
            IsBound = Free.Count == 0;
        }

        public void Bind(JsonArray typeParams, JsonArray methodParams)
        {
            // A bound may itself mention another TV (`S : Segment<S>` or `T : Pair<T,U>`). Those variables are part
            // of the generated cell's signature too, even when they do not occur directly in the element type.
            for (var i = 0; i < Free.Count; i++)
            {
                var key = Free[i];
                var source = key.Scope == "type" ? typeParams : methodParams;
                if (key.Index < 0 || key.Index >= source.Count || source[key.Index] is not JsonNode descriptor)
                    throw new InvalidOperationException(
                        $"ref-cell `{Name}` cannot resolve {key.Scope} type variable #{key.Index} in its lexical owner");

                var normalized = NormalizeDescriptor(descriptor);
                if (Descriptors.TryGetValue(key, out var prior))
                {
                    if (prior.ToJsonString() != normalized.ToJsonString())
                        throw new InvalidOperationException(
                            $"ref-cell `{Name}` is used under incompatible constraints for {key.Scope} type variable #{key.Index}");
                }
                else
                {
                    Descriptors.Add(key, normalized);
                    if (normalized is JsonObject no && no["constraints"] is JsonArray constraints)
                        foreach (var constraint in constraints)
                            AddFreeTvs(constraint, Free);
                    SortFree();
                }
            }
            IsBound = true;
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

    static JsonArray ParamsOf(JsonObject declaration) =>
        declaration["typeParams"] as JsonArray ?? new JsonArray();

    static void BindRefCellContexts(JsonObject file, IReadOnlyDictionary<string, RefCellSpec> specs)
    {
        var noParams = new JsonArray();
        if (file["fields"] is JsonNode fields) BindRefsIn(fields, specs, noParams, noParams);
        if (file["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
                BindRefsIn(method, specs, noParams, ParamsOf(method));

        if (file["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>())
                BindType(type, specs);
    }

    static void BindType(JsonObject type, IReadOnlyDictionary<string, RefCellSpec> specs)
    {
        var typeParams = ParamsOf(type);
        PrepareSyntheticRefUses(type, specs);

        // Scan the type header/fields without descending through member declarations under the wrong method context.
        foreach (var kv in type)
        {
            if (kv.Key is "methods" or "ctors" or "types" || kv.Value == null) continue;
            BindRefsIn(kv.Value, specs, typeParams, new JsonArray());
        }

        if (type["ctors"] is JsonArray ctors)
            foreach (var ctor in ctors.OfType<JsonObject>())
                BindRefsIn(ctor, specs, typeParams, ParamsOf(ctor));
        if (type["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
                BindRefsIn(method, specs, typeParams, ParamsOf(method));
        if (type["types"] is JsonArray nested)
            foreach (var child in nested.OfType<JsonObject>())
                BindType(child, specs);
    }

    static void BindRefsIn(
        JsonNode node,
        IReadOnlyDictionary<string, RefCellSpec> specs,
        JsonArray typeParams,
        JsonArray methodParams)
    {
        switch (node)
        {
            case JsonObject o:
                if (Str(o["t"]) == "fqn" && Str(o["name"]) is string name && specs.TryGetValue(name, out var spec))
                {
                    // A lifted synthetic class has already constructed this use in its new type-parameter scope.
                    // Its lexical source constraints were bound at the original capture/new site.
                    if (o["args"] == null) spec.Bind(typeParams, methodParams);
                }
                foreach (var kv in o)
                    if (kv.Value != null) BindRefsIn(kv.Value, specs, typeParams, methodParams);
                break;
            case JsonArray a:
                foreach (var item in a)
                    if (item != null) BindRefsIn(item, specs, typeParams, methodParams);
                break;
        }
    }

    static void PrepareSyntheticRefUses(JsonObject type, IReadOnlyDictionary<string, RefCellSpec> specs)
    {
        if (type["_syntheticTypeArgs"] is not JsonArray origins) return;
        var positions = new Dictionary<TvKey, int>();
        for (var i = 0; i < origins.Count; i++)
            if (origins[i] is JsonObject tv && Str(tv["t"]) == "tv" && Str(tv["scope"]) is string scope
                && tv["i"] is JsonValue iv && iv.TryGetValue<int>(out var index))
                positions.TryAdd(new TvKey(scope, index), i);

        void Walk(JsonNode node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (Str(o["t"]) == "fqn" && Str(o["name"]) is string name
                        && specs.TryGetValue(name, out var spec) && spec.Free.Count != 0 && o["args"] == null)
                    {
                        var args = new JsonArray();
                        foreach (var key in spec.Free)
                        {
                            if (!positions.TryGetValue(key, out var position))
                                throw new InvalidOperationException(
                                    $"generic ref-cell `{name}` in synthetic class `{Str(type["name"])}` "
                                    + $"cannot map captured {key.Scope} type variable #{key.Index}");
                            args.Add(new JsonObject { ["t"] = "tv", ["scope"] = "type", ["i"] = position });
                        }
                        o["args"] = args;
                    }
                    foreach (var kv in o)
                        if (kv.Key != "_syntheticTypeArgs" && kv.Value != null) Walk(kv.Value);
                    break;
                case JsonArray a:
                    foreach (var item in a)
                        if (item != null) Walk(item);
                    break;
            }
        }

        Walk(type);
        type.Remove("_syntheticTypeArgs");
    }

    static void RewriteRefCellUses(JsonNode node, IReadOnlyDictionary<string, RefCellSpec> specs)
    {
        switch (node)
        {
            case JsonObject o:
                if (Str(o["t"]) == "fqn" && Str(o["name"]) is string name
                    && specs.TryGetValue(name, out var spec) && spec.Free.Count != 0)
                {
                    if (o["args"] == null)
                        o["args"] = new JsonArray(spec.Free.Select(k => (JsonNode)new JsonObject
                        {
                            ["t"] = "tv",
                            ["scope"] = k.Scope,
                            ["i"] = k.Index,
                        }).ToArray());
                }
                foreach (var kv in o)
                    if (kv.Value != null) RewriteRefCellUses(kv.Value, specs);
                break;
            case JsonArray a:
                foreach (var item in a)
                    if (item != null) RewriteRefCellUses(item, specs);
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

    // A heap cell `class <name><T…>(var v: elem)` — a single field + its init ctor. Closed elements retain the old
    // monomorphic byte shape; open elements become a constrained generic cell constructed at every lexical use.
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

    // Fixed-shape def transcribed verbatim from kotc's retired charSeqIfaceDefs().
    const string CharSeqDef = """
    {"name":"dotkt$CharSequence","kind":"interface","generated":true,"base":null,"fields":[],"ctors":[],"methods":[
      {"name":"get_length","static":false,"override":false,"virtual":true,"objectOverride":false,"vis":"public","params":[],"ret":{"t":"fqn","name":"kotlin.Int"},"body":[]},
      {"name":"get","static":false,"override":false,"virtual":true,"objectOverride":false,"vis":"public","params":[{"name":"index","type":{"t":"fqn","name":"kotlin.Int"}}],"ret":{"t":"fqn","name":"kotlin.Char"},"body":[]},
      {"name":"subSequence","static":false,"override":false,"virtual":true,"objectOverride":false,"vis":"public","params":[{"name":"startIndex","type":{"t":"fqn","name":"kotlin.Int"}},{"name":"endIndex","type":{"t":"fqn","name":"kotlin.Int"}}],"ret":{"t":"fqn","name":"dotkt$CharSequence"},"body":[]}
    ]}
    """;
}
