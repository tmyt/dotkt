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

        // `_syntheticTypeArgs` is a TRANSIENT lifted-frame correspondence, consumed above and never part of CIR. The
        // consumer above only runs for a file that HAS a ref-cell registry, so drop any remaining one here: both
        // producers (kotc's lifted local `fun`, ClosureSynthesis's lifted closure class) emit it whenever the lifted
        // synthetic is generic, ref cells or not.
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

        public void Bind(JsonArray typeParams, JsonArray methodParams) =>
            Bind(key =>
            {
                var source = key.Scope == "type" ? typeParams : methodParams;
                return key.Index >= 0 && key.Index < source.Count ? source[key.Index] : null;
            });

        /// A LIFTED SYNTHETIC (a closure class, or a lifted local fun) re-declares the enclosing type params as its
        /// own, WITH their constraints, and records which original variable each of its own params stands for. That
        /// makes it an equally valid descriptor source — and the only one when the celled `var` is declared inside the
        /// lift, so no use survives in the frame that originally declared the variable.
        public void BindThroughSynthetic(JsonArray typeParams, JsonArray methodParams,
            IReadOnlyDictionary<TvKey, TypeNode> bindings) =>
            Bind(key =>
            {
                if (!bindings.TryGetValue(key, out var target) || target is not TypeNode.Tv tv) return null;
                var source = tv.Scope == "type" ? typeParams : methodParams;
                return tv.I >= 0 && tv.I < source.Count ? source[tv.I] : null;
            });

        void Bind(Func<TvKey, JsonNode> descriptorOf)
        {
            // A bound may itself mention another TV (`S : Segment<S>` or `T : Pair<T,U>`). Those variables are part
            // of the generated cell's signature too, even when they do not occur directly in the element type.
            for (var i = 0; i < Free.Count; i++)
            {
                var key = Free[i];
                if (descriptorOf(key) is not JsonNode descriptor)
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

    /// A bare cell identity inside a LIFTED SYNTHETIC, together with the frame it must be constructed in: the
    /// synthetic's available type/method frames and the exact physical slot to which each original variable moved.
    sealed record SyntheticUse(
        JsonObject Node, string OwnerName, JsonArray TypeParams, JsonArray MethodParams,
        Dictionary<TvKey, TypeNode> Bindings);

    /// Recover the lexical type-parameter descriptors for every generic cell, then construct each of its uses.
    ///
    /// Three passes, because the two halves are mutually dependent: constructing a use inside a lifted synthetic needs
    /// the FINAL parameter list (binding a constraint that mentions a further variable can extend it), while binding
    /// must not treat a use inside a lifted synthetic as if the original frame's variables resolved there. So: collect
    /// the synthetic-frame uses, bind everything, then construct them. Doing it per-declaration in one walk made the
    /// result depend on declaration ORDER — a use stamped before a later constraint extended the list got too few
    /// arguments.
    static void BindRefCellContexts(JsonObject file, IReadOnlyDictionary<string, RefCellSpec> specs)
    {
        var syntheticUses = new List<SyntheticUse>();
        // The collected uses, by reference: binding must SKIP them, because the variables their cell element names do
        // not resolve in the frame they sit in — that is precisely why they need constructing from the correspondence.
        var deferred = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        var ctx = new BindContext(specs, syntheticUses, deferred);
        var noParams = new JsonArray();

        if (file["fields"] is JsonNode fields) BindRefsIn(fields, ctx, noParams, noParams);
        if (file["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
                BindMethod(method, ctx, noParams);
        if (file["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>())
                BindType(type, ctx);

        // Every lifted synthetic re-declared its enclosing params WITH their constraints, so it can supply the
        // descriptors when the celled `var` is declared inside the lift and no use survives in the declaring frame.
        foreach (var use in syntheticUses)
            if (specs.TryGetValue(Str(use.Node["name"]), out var spec) && !spec.IsBound)
                spec.BindThroughSynthetic(use.TypeParams, use.MethodParams, use.Bindings);

        foreach (var use in syntheticUses)
            ConstructSyntheticRefUse(use, specs);
    }

    sealed record BindContext(
        IReadOnlyDictionary<string, RefCellSpec> Specs, List<SyntheticUse> SyntheticUses, HashSet<JsonObject> Deferred);

    static void BindMethod(JsonObject method, BindContext ctx, JsonArray typeParams)
    {
        // A lifted local `fun` is a static method that re-declares its enclosing declaration's free type params as its
        // OWN METHOD params (kotc carries the correspondence in `_syntheticTypeArgs`), so a bare cell identity used
        // here must construct in the METHOD parameter space — the twin of a lifted closure CLASS constructing in its
        // type parameter space. Without this the cell's enclosing-frame `tv` would be looked up in a frame that does
        // not declare it (a file-class method has no enclosing type params at all).
        CollectSyntheticRefUses(method, ctx, "method", typeParams);
        BindRefsIn(method, ctx, typeParams, ParamsOf(method));
    }

    static void BindType(JsonObject type, BindContext ctx)
    {
        var typeParams = ParamsOf(type);
        CollectSyntheticRefUses(type, ctx, "type", new JsonArray());

        // Scan the type header/fields without descending through member declarations under the wrong method context.
        foreach (var kv in type)
        {
            if (kv.Key is "methods" or "ctors" or "types" || kv.Value == null) continue;
            BindRefsIn(kv.Value, ctx, typeParams, new JsonArray());
        }

        if (type["ctors"] is JsonArray ctors)
            foreach (var ctor in ctors.OfType<JsonObject>())
                BindRefsIn(ctor, ctx, typeParams, ParamsOf(ctor));
        if (type["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
                BindMethod(method, ctx, typeParams);
        if (type["types"] is JsonArray nested)
            foreach (var child in nested.OfType<JsonObject>())
                BindType(child, ctx);
    }

    static void BindRefsIn(JsonNode node, BindContext ctx, JsonArray typeParams, JsonArray methodParams)
    {
        switch (node)
        {
            case JsonObject o:
                if (Str(o["t"]) == "fqn" && Str(o["name"]) is string name && ctx.Specs.TryGetValue(name, out var spec))
                {
                    // Already constructed (an inline-specialized use), or DEFERRED to its lifted synthetic's frame —
                    // in both cases this frame is not where the cell element's variables resolve.
                    if (o["args"] == null && !ctx.Deferred.Contains(o)) spec.Bind(typeParams, methodParams);
                }
                foreach (var kv in o)
                    if (kv.Value != null) BindRefsIn(kv.Value, ctx, typeParams, methodParams);
                break;
            case JsonArray a:
                foreach (var item in a)
                    if (item != null) BindRefsIn(item, ctx, typeParams, methodParams);
                break;
        }
    }

    // `_syntheticTypeArgs` records, positionally, which ORIGINAL enclosing type variable each of a lifted synthetic's
    // own type params re-declares. Two producers, one meaning: kotc emits it on a lifted local `fun` (whose retained
    // variables move into its METHOD params while owner variables stay in the containing TYPE frame), and bir2cir's
    // ClosureSynthesis derives it for a lifted closure CLASS (whose variables all move into its TYPE params).
    static void CollectSyntheticRefUses(JsonObject owner, BindContext ctx, string newScope,
        JsonArray containingTypeParams)
    {
        if (owner["_syntheticTypeArgs"] is not JsonArray origins) return;
        var bindings = new Dictionary<TvKey, TypeNode>();
        for (var i = 0; i < origins.Count; i++)
            if (origins[i] is JsonObject tv && Str(tv["t"]) == "tv" && Str(tv["scope"]) is string scope
                && tv["i"] is JsonValue iv && iv.TryGetValue<int>(out var index))
                bindings.TryAdd(new TvKey(scope, index), new TypeNode.Tv(newScope, i));
        var ownerName = Str(owner["name"]);
        var ownParams = ParamsOf(owner);
        var typeParams = newScope == "type" ? ownParams : containingTypeParams;
        var methodParams = newScope == "method" ? ownParams : new JsonArray();
        // LocalFunctionLowering consumes owner origins from the method-generic vector because the constructed owner
        // supplies them. Their uses now name the containing type frame directly. A cell declared inside a compacted
        // local function can likewise already name its new dense method slot. Preserve both identity edges in addition
        // to the sparse origin correspondence above; TryAdd leaves an explicit non-identity origin authoritative.
        for (var i = 0; i < typeParams.Count; i++)
            bindings.TryAdd(new TvKey("type", i), new TypeNode.Tv("type", i));
        for (var i = 0; i < methodParams.Count; i++)
            bindings.TryAdd(new TvKey("method", i), new TypeNode.Tv("method", i));

        void Walk(JsonNode node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (Str(o["t"]) == "fqn" && Str(o["name"]) is string name
                        && ctx.Specs.TryGetValue(name, out var spec) && spec.Free.Count != 0 && o["args"] == null)
                    {
                        ctx.SyntheticUses.Add(new SyntheticUse(o, ownerName, typeParams, methodParams, bindings));
                        ctx.Deferred.Add(o);
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

        Walk(owner);
        owner.Remove("_syntheticTypeArgs");
    }

    /// Construct one collected use in its lifted synthetic's own parameter space. Runs after ALL binding, so
    /// `spec.Free` is final and the argument list cannot come out short.
    static void ConstructSyntheticRefUse(SyntheticUse use, IReadOnlyDictionary<string, RefCellSpec> specs)
    {
        if (use.Node["args"] != null) return;
        if (Str(use.Node["name"]) is not string name || !specs.TryGetValue(name, out var spec)) return;
        var args = new JsonArray();
        foreach (var key in spec.Free)
        {
            if (!use.Bindings.TryGetValue(key, out var argument))
                throw new InvalidOperationException(
                    $"generic ref-cell `{name}` in lifted synthetic `{use.OwnerName}` "
                    + $"cannot map captured {key.Scope} type variable #{key.Index}");
            args.Add(TypeJson.Write(argument));
        }
        use.Node["args"] = args;
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
