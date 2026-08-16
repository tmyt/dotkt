using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// BIR keeps a Kotlin local function as a lexical `{k:"localFun",id,decl}` declaration. Calls and references name
// that declaration explicitly (`callLocal` / `localFunRef`). This pass consumes only those authored semantic facts
// and selects the CLR representation: a compiler-named static MethodDef on the nearest source TypeDef (or the file
// facade for a top-level declaration / an erased @ClrTypeAlias owner). No method name, FileClass placement, or body
// shape is inspected to reconstruct ownership.
static class LocalFunctionLowering
{
    sealed record Binding(string Name, string Owner, int[] OwnerArgPositions, int[] SemanticOwnerArgOrder);

    static string Str(JsonNode node) => (node as JsonValue)?.GetValue<string>();

    public static void Apply(JsonObject file, ReferenceMetadataIndex refs)
    {
        var fileClass = Str(file["fileClass"])
            ?? throw new InvalidOperationException("BIR file has no fileClass while lowering local functions");
        var fileMethods = file["methods"] as JsonArray ?? new JsonArray();
        file["methods"] = fileMethods;
        var types = (file["types"] as JsonArray)?.OfType<JsonObject>()
            .Where(type => Str(type["name"]) != null)
            .ToDictionary(type => Str(type["name"]), StringComparer.Ordinal)
            ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var refCells = (file["refTypes"] as JsonArray)?.OfType<JsonObject>()
            .Where(cell => Str(cell["name"]) != null && cell["elem"] != null)
            .ToDictionary(cell => Str(cell["name"]), StringComparer.Ordinal)
            ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        var counter = 0;

        JsonArray TargetMethods(string semanticOwner, out string physicalOwner, out JsonObject ownerType)
        {
            ownerType = null;
            if (semanticOwner == fileClass
                || refs.Aliases.ContainsKey(ReferenceMetadataIndex.BareOwnerFqn(semanticOwner)))
            {
                physicalOwner = fileClass;
                return fileMethods;
            }
            if (!types.TryGetValue(semanticOwner, out ownerType))
                throw new InvalidOperationException(
                    $"local function has missing semantic source type '{semanticOwner}'");
            physicalOwner = semanticOwner;
            var methods = ownerType["methods"] as JsonArray;
            if (methods == null) { methods = new JsonArray(); ownerType["methods"] = methods; }
            return methods;
        }

        void CollectArray(JsonArray array, string semanticOwner)
        {
            for (var i = 0; i < array.Count;)
            {
                if (array[i] is JsonObject local && Str(local["k"]) == "localFun")
                {
                    if (Str(local["id"]) is not string id || local["decl"] is not JsonObject source)
                        throw new InvalidOperationException("malformed BIR localFun declaration");
                    var target = TargetMethods(semanticOwner, out var physicalOwner, out var ownerType);
                    var sourceName = Str(source["sourceName"]) ?? "local";
                    var physicalName = $"dotkt$local{counter++}_{sourceName}";
                    var declaration = source.DeepClone() as JsonObject;
                    declaration.Remove("sourceName");
                    declaration["name"] = physicalName;
                    declaration["generated"] = true;
                    var binding = PrepareGenericOwnerBinding(declaration, physicalOwner, ownerType, id, refCells);
                    binding = binding with { SemanticOwnerArgOrder = SemanticOwnerArgOrder(ownerType) };
                    binding = binding with { Name = physicalName, Owner = physicalOwner };
                    if (!bindings.TryAdd(id, binding))
                        throw new InvalidOperationException($"duplicate BIR local function declaration id '{id}'");
                    // The declaration is registered before its body is visited so recursion is an ordinary id edge.
                    CollectNode(declaration, semanticOwner);
                    target.Add(declaration);
                    array.RemoveAt(i);       // a declaration has no run-time statement
                    continue;
                }
                if (array[i] is JsonNode item) CollectNode(item, semanticOwner);
                i++;
            }
        }

        // Owner declarations are already normalized to CLR slot order [outermost..., own...] before this pass, while
        // the TypeNode authored here is still BIR and must use Kotlin inner order [own..., immediate owner..., ...].
        // Derive the complete group permutation from the explicit semanticOwner/outerTypeParamCount chain. A single
        // rotation works only at depth one; Leaf<C> under Middle<B> under Outer<A> requires [C,B,A], not [C,A,B].
        int[] SemanticOwnerArgOrder(JsonObject ownerType)
        {
            if (ownerType?["typeParams"] is not JsonArray parameters)
                return Array.Empty<int>();
            var total = parameters.Count;
            if (ownerType["mods"] is not JsonObject mods
                || mods["inner"] is not JsonValue innerValue
                || !innerValue.TryGetValue<bool>(out var isInner) || !isInner
                || ownerType["outerTypeParamCount"] is not JsonValue countValue
                || !countValue.TryGetValue<int>(out var captured) || captured == 0)
                return Enumerable.Range(0, total).ToArray();
            if (captured > total || Str(ownerType["semanticOwner"]) is not string parentName
                || !types.TryGetValue(parentName, out var parentType))
                throw new InvalidOperationException(
                    $"inner local-function owner '{Str(ownerType["name"])}' has an invalid semantic owner frame");
            var parentOrder = SemanticOwnerArgOrder(parentType);
            if (parentOrder.Length != captured)
                throw new InvalidOperationException(
                    $"inner local-function owner '{Str(ownerType["name"])}' captures {captured} slots, " +
                    $"but its semantic owner declares {parentOrder.Length}");
            return Enumerable.Range(captured, total - captured)
                .Concat(parentOrder)
                .ToArray();
        }

        void CollectNode(JsonNode node, string semanticOwner)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var pair in obj.ToList())
                    {
                        if (pair.Value == null) continue;
                        if (pair.Value is JsonArray childArray) CollectArray(childArray, semanticOwner);
                        else CollectNode(pair.Value, semanticOwner);
                    }
                    break;
                case JsonArray array:
                    CollectArray(array, semanticOwner);
                    break;
            }
        }

        void RewriteUses(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                {
                    var kind = Str(obj["k"]);
                    if (kind is "callLocal" or "localFunRef")
                    {
                        if (Str(obj["id"]) is not string id || !bindings.TryGetValue(id, out var binding))
                            throw new InvalidOperationException($"unbound BIR {kind} declaration id '{Str(obj["id"])}'");
                        RewriteUse(obj, kind, binding);
                    }
                    foreach (var child in obj.Select(pair => pair.Value).Where(value => value != null).ToList())
                        RewriteUses(child);
                    break;
                }
                case JsonArray array:
                    foreach (var child in array.Where(value => value != null).ToList()) RewriteUses(child);
                    break;
            }
        }

        // Top-level executable declarations use the file facade. Type-member executable declarations use that source
        // type even when their body contains a closure/suspend-lambda ingredient bag: those bags are representation
        // inputs, not Kotlin declaration owners.
        foreach (var method in fileMethods.ToList().OfType<JsonObject>())
            CollectNode(method, fileClass);
        if (file["fields"] is JsonArray fields)
            foreach (var field in fields.OfType<JsonObject>()) CollectNode(field, fileClass);
        foreach (var type in types.Values.ToList())
        {
            var owner = Str(type["name"]);
            if (type["methods"] is JsonArray methods)
                foreach (var method in methods.ToList().OfType<JsonObject>()) CollectNode(method, owner);
            if (type["ctors"] is JsonArray ctors)
                foreach (var ctor in ctors.OfType<JsonObject>()) CollectNode(ctor, owner);
            if (type["fields"] is JsonArray typeFields)
                foreach (var field in typeFields.OfType<JsonObject>()) CollectNode(field, owner);
            if (type["properties"] is JsonArray properties)
                foreach (var property in properties.OfType<JsonObject>()) CollectNode(property, owner);
        }
        RewriteUses(file);
    }

    static Binding PrepareGenericOwnerBinding(JsonObject method, string owner, JsonObject ownerType,
        string declarationId, IReadOnlyDictionary<string, JsonObject> refCells)
    {
        var ownerArity = ownerType?["typeParams"] is JsonArray ownerTypeParams ? ownerTypeParams.Count : 0;
        var ownerArgPositions = Enumerable.Repeat(-1, ownerArity).ToArray();

        // A local function materialized as a static MethodDef on Owner<T> still has Owner<T>'s lexical type frame.
        // A later suspend lowering moves its body again, into a nested state-machine TypeDef, and therefore needs the
        // complete owner prefix even when the local declaration happened not to mention every slot. Carry that exact
        // representation decision forward explicitly; SuspendColdLowering consumes it instead of inferring owner use
        // from the method body or generated name. Non-suspend locals need no later declaration move.
        if (ownerArity > 0
            && method["mods"] is JsonObject methodMods
            && methodMods["suspend"] is JsonValue suspendValue
            && suspendValue.TryGetValue<bool>(out var isSuspend)
            && isSuspend)
            method["lexicalOwnerTypeParamCount"] = ownerArity;

        // kotc explicitly carries the correspondence between this local declaration's re-declared free parameters and
        // their lexical origins. Owner type slots are supplied by the declaring TypeSpec; only the remaining origins
        // stay MethodSpec parameters. The retained origins can be sparse (an enclosing <A,B> whose local uses only B),
        // so compact every reference in the new method frame at the same time. This consumes an authored fact, never
        // derives one from names.
        if (method["_syntheticTypeArgs"] is JsonArray origins
            && method["typeParams"] is JsonArray methodTypeParams
            && origins.Count == methodTypeParams.Count)
        {
            var keep = new List<int>();
            for (var p = 0; p < origins.Count; p++)
            {
                var origin = origins[p] as JsonObject;
                var slot = -1;
                var isOwnerSlot = origin != null && Str(origin["t"]) == "tv"
                    && Str(origin["scope"]) == "type"
                    && origin["i"] is JsonValue iv && iv.TryGetValue<int>(out slot)
                    && slot >= 0 && slot < ownerArity;
                if (isOwnerSlot) ownerArgPositions[slot] = p;
                else keep.Add(p);
            }
            // kotc has already expressed the local declaration in its own dense method frame. Origins are used only
            // to identify owner-type slots; two different lexical methods may both have authored `method#0`, so an
            // origin tuple is not an identity and must never be used as this remapping key.
            var capturedToPhysical = new Dictionary<int, TypeNode>();
            var nextMethodSlot = 0;
            for (var p = 0; p < origins.Count; p++)
            {
                var origin = origins[p] as JsonObject;
                if (origin == null || Str(origin["t"]) != "tv" || Str(origin["scope"]) is not string
                    || origin["i"] is not JsonValue indexValue || !indexValue.TryGetValue<int>(out _))
                    throw new InvalidOperationException(
                        $"local function '{Str(method["sourceName"]) ?? Str(method["name"])}' has a non-variable synthetic type origin");
                var slot = -1;
                var isOwnerSlot = Str(origin["scope"]) == "type"
                    && origin["i"] is JsonValue iv && iv.TryGetValue<int>(out slot)
                    && slot >= 0 && slot < ownerArity;
                capturedToPhysical[p] = isOwnerSlot
                    ? new TypeNode.Tv("type", slot)
                    : new TypeNode.Tv("method", nextMethodSlot++);
            }
            method["typeParams"] = new JsonArray(keep.Select(p => methodTypeParams[p]?.DeepClone()).ToArray());
            // Preserve each retained parameter's ORIGINAL lexical key. SharedSyntheticSynthesis uses this authored
            // correspondence to construct a file-registry ref cell in the new dense method frame; replacing sparse
            // method#N origins with method#0 here loses the only edge back to the registry element declaration.
            method["_syntheticTypeArgs"] = new JsonArray(keep
                .Select(position => origins[position]?.DeepClone()).ToArray());
            // A cell DECLARED in this local function was registered in this declaration's dense method frame, unlike
            // a cell merely captured from an enclosing sparse frame. kotc identifies that ownership edge explicitly;
            // only those registry elements move with this declaration.
            foreach (var cell in refCells.Values)
                if (Str(cell["declaringLocalFunctionId"]) == declarationId)
                {
                    cell["elem"] = TypeJson.Write(
                        RewriteCapturedType(TypeJson.Read(cell["elem"]), capturedToPhysical));
                    cell.Remove("declaringLocalFunctionId");
                }
            RewriteCapturedTypeVariables(method, capturedToPhysical);
            if (keep.Count == 0)
            {
                method.Remove("typeParams");
                method.Remove("_syntheticTypeArgs");
            }
        }
        return new Binding(null, owner, ownerArgPositions, Array.Empty<int>());
    }

    static void RewriteCapturedTypeVariables(
        JsonNode node, IReadOnlyDictionary<int, TypeNode> capturedToPhysical)
    {
        if (node is JsonObject obj)
        {
            var kind = Str(obj["k"]);
            // A nested localFun declaration owns an independent dense method-type frame. It is collected and
            // normalized separately, after this declaration has been registered for recursive references; allowing
            // the outer remap to enter its `decl` would reinterpret the inner method#0 as the outer method#0.
            if (kind == "localFun") return;
            foreach (var key in obj.Select(pair => pair.Key).ToList())
            {
                var value = obj[key];
                if (value == null) continue;
                // These vectors are expressed in the referenced declaration's generic frame, not this local method's
                // frame. `_syntheticTypeArgs` deliberately retains the original lexical ids for later ref-cell binding.
                if (key is "sig" or "resolvedMemberParams" or "shapeTypes" or "paramSig"
                    or "delegationSig" or "_syntheticTypeArgs" || (key == "argTypes" && kind != "new"))
                    continue;
                if (TypeJson.IsType(value)) obj[key] = TypeJson.Write(RewriteCapturedType(TypeJson.Read(value), capturedToPhysical));
                else RewriteCapturedTypeVariables(value, capturedToPhysical);
            }
        }
        else if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++)
            {
                var value = array[i];
                if (value == null) continue;
                if (TypeJson.IsType(value)) array[i] = TypeJson.Write(RewriteCapturedType(TypeJson.Read(value), capturedToPhysical));
                else RewriteCapturedTypeVariables(value, capturedToPhysical);
            }
    }

    static TypeNode RewriteCapturedType(TypeNode type, IReadOnlyDictionary<int, TypeNode> capturedToPhysical) =>
        type switch
        {
            TypeNode.Tv tv when tv.Scope == "method" && capturedToPhysical.TryGetValue(tv.I, out var physical) =>
                physical,
            TypeNode.Fqn f => new TypeNode.Fqn(f.Name,
                f.Args?.Select(arg => RewriteCapturedType(arg, capturedToPhysical)).ToArray()),
            TypeNode.Nullable n => new TypeNode.Nullable(RewriteCapturedType(n.Of, capturedToPhysical)),
            TypeNode.Oblivious o => new TypeNode.Oblivious(RewriteCapturedType(o.Of, capturedToPhysical)),
            TypeNode.Array a => new TypeNode.Array(RewriteCapturedType(a.Elem, capturedToPhysical)),
            TypeNode.ByRef b => new TypeNode.ByRef(RewriteCapturedType(b.Of, capturedToPhysical)),
            TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend,
                RewriteCapturedType(fn.Ret, capturedToPhysical),
                fn.Params.Select(param => RewriteCapturedType(param, capturedToPhysical)).ToArray(),
                fn.Recv == null ? null : RewriteCapturedType(fn.Recv, capturedToPhysical), fn.Clr,
                fn.Ctx?.Select(param => RewriteCapturedType(param, capturedToPhysical)).ToArray()),
            _ => type,
        };

    static void RewriteUse(JsonObject use, string kind, Binding binding)
    {
        var callTypeArgs = use["typeArgs"] as JsonArray;
        var ownerArgs = Enumerable.Range(0, binding.OwnerArgPositions.Length).Select(slot =>
        {
            var position = binding.OwnerArgPositions[slot];
            return position >= 0 && callTypeArgs != null && position < callTypeArgs.Count
                ? TypeJson.Read(callTypeArgs[position])
                : (TypeNode)new TypeNode.Tv("type", slot);
        }).ToArray();
        // ProjectInnerApplications still consumes Kotlin's inner-class application order [own..., outer...]. The owner
        // declaration is already normalized to physical [outer..., own...] here, so translate this newly-authored BIR
        // application back to the semantic order exactly once; the later projection restores the physical order.
        var semanticOwnerArgs = binding.SemanticOwnerArgOrder.Length == ownerArgs.Length
            ? binding.SemanticOwnerArgOrder.Select(index => ownerArgs[index]).ToArray()
            : ownerArgs;
        use["calleeOwner"] = TypeJson.Write(ownerArgs.Length == 0
            ? new TypeNode.Fqn(binding.Owner)
            : new TypeNode.Fqn(binding.Owner, semanticOwnerArgs));
        use["method"] = binding.Name;
        use.Remove("id");

        if (callTypeArgs != null)
        {
            var consumed = binding.OwnerArgPositions.Where(position => position >= 0).ToHashSet();
            var remaining = new JsonArray(Enumerable.Range(0, callTypeArgs.Count)
                .Where(position => !consumed.Contains(position))
                .Select(position => callTypeArgs[position]?.DeepClone()).ToArray());
            if (remaining.Count == 0) use.Remove("typeArgs");
            else use["typeArgs"] = remaining;
        }

        if (kind == "callLocal")
        {
            use["k"] = "callStatic";
            use["owner"] = null;
        }
        else use["k"] = "newDelegate";
    }
}
