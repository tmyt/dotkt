using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Kotlin declarations carry only their semantic lexical owner in BIR. This pass is the single place that selects
// the corresponding CLR TypeDef nesting and rewrites inner-class outer generic declarations into explicit physical
// capture slots. ilemit then emits nestedIn/capturedTypeParams one-to-one.
static class TypeOwnershipLowering
{
    static string Str(JsonNode node) => (node as JsonValue)?.GetValue<string>();

    // Lifted local/anonymous implementation types keep Kotlin semantic generic order in BIR:
    // [own..., lexical-owner..., other captured...]. CLR nested TypeDefs require the lexical-owner segment first.
    // Project the declaration slots, every type-scope tv in the declaration, and every construction/application as
    // one module-wide permutation before any lowering pass consumes positional generic facts.
    static void NormalizeOwnerCapturePrefixes(IReadOnlyList<JsonNode> roots)
    {
        var permutations = new Dictionary<string, (int[] NewToOld, int[] OldToNew)>(StringComparer.Ordinal);

        static TypeNode RemapTvs(TypeNode type, int[] oldToNew) => type switch
        {
            TypeNode.Tv tv when tv.Scope == "type" && tv.I >= 0 && tv.I < oldToNew.Length =>
                new TypeNode.Tv("type", oldToNew[tv.I]),
            TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args?.Select(a => RemapTvs(a, oldToNew)).ToArray()),
            TypeNode.Nullable n => new TypeNode.Nullable(RemapTvs(n.Of, oldToNew)),
            TypeNode.Oblivious o => new TypeNode.Oblivious(RemapTvs(o.Of, oldToNew)),
            TypeNode.Array a => new TypeNode.Array(RemapTvs(a.Elem, oldToNew)),
            TypeNode.ByRef b => new TypeNode.ByRef(RemapTvs(b.Of, oldToNew)),
            TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, RemapTvs(fn.Ret, oldToNew),
                fn.Params.Select(p => RemapTvs(p, oldToNew)).ToArray(),
                fn.Recv == null ? null : RemapTvs(fn.Recv, oldToNew), fn.Clr,
                fn.Ctx?.Select(p => RemapTvs(p, oldToNew)).ToArray()),
            _ => type,
        };

        static void RewriteTypes(JsonNode node, Func<TypeNode, TypeNode> rewrite,
            bool lexicalTypeVariablesOnly = false)
        {
            if (node is JsonObject obj)
            {
                var kind = Str(obj["k"]);
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var value = obj[key];
                    if (value == null) continue;
                    // These vectors describe another declaration in that declaration's own generic frame. Reordering
                    // the current lexical type's slots must never rewrite them. Actual applications (`ownerType`,
                    // `typeArgs`, value types, etc.) remain in the walk. `new.argTypes` describes an application at the
                    // current use site and is rewritten; descriptor argTypes on calls/members remain in the referenced
                    // declaration's own frame.
                    if (lexicalTypeVariablesOnly && (key is "sig" or "memberSig" or "clrOverrideSig"
                        or "shapeTypes" or "paramSig" or "delegationSig" || (key == "argTypes" && kind != "new")))
                        continue;
                    if (TypeJson.IsType(value)) obj[key] = TypeJson.Write(rewrite(TypeJson.Read(value)));
                    else RewriteTypes(value, rewrite, lexicalTypeVariablesOnly);
                }
            }
            else if (node is JsonArray array)
                for (var i = 0; i < array.Count; i++)
                {
                    var value = array[i];
                    if (value == null) continue;
                    if (TypeJson.IsType(value)) array[i] = TypeJson.Write(rewrite(TypeJson.Read(value)));
                    else RewriteTypes(value, rewrite, lexicalTypeVariablesOnly);
                }
        }

        foreach (var root in roots.OfType<JsonObject>())
        {
            if (root["types"] is not JsonArray declarations) continue;
            foreach (var type in declarations.OfType<JsonObject>())
            {
                if (type["outerTypeParamOffset"] is not JsonValue offsetValue
                    || !offsetValue.TryGetValue<int>(out var offset) || offset == 0)
                {
                    type.Remove("outerTypeParamOffset");
                    continue;
                }
                if (type["outerTypeParamCount"] is not JsonValue countValue
                    || !countValue.TryGetValue<int>(out var count) || count <= 0
                    || type["typeParams"] is not JsonArray typeParams
                    || offset < 0 || offset + count > typeParams.Count)
                    throw new InvalidOperationException(
                        $"Kotlin type '{Str(type["name"])}' has an invalid semantic owner parameter segment");

                var newToOld = Enumerable.Range(offset, count)
                    .Concat(Enumerable.Range(0, offset))
                    .Concat(Enumerable.Range(offset + count, typeParams.Count - offset - count))
                    .ToArray();
                var oldToNew = new int[newToOld.Length];
                for (var i = 0; i < newToOld.Length; i++) oldToNew[newToOld[i]] = i;
                var reordered = new JsonArray(newToOld.Select(i => typeParams[i]?.DeepClone()).ToArray());
                type["typeParams"] = reordered;
                type.Remove("outerTypeParamOffset");
                RewriteTypes(type, t => RemapTvs(t, oldToNew), lexicalTypeVariablesOnly: true);

                if (Str(type["name"]) is string name)
                    permutations[name] = (newToOld, oldToNew);
            }
        }

        if (permutations.Count == 0) return;

        TypeNode RewriteApplications(TypeNode type) => type switch
        {
            TypeNode.Fqn f => RewriteFqn(f),
            TypeNode.Nullable n => new TypeNode.Nullable(RewriteApplications(n.Of)),
            TypeNode.Oblivious o => new TypeNode.Oblivious(RewriteApplications(o.Of)),
            TypeNode.Array a => new TypeNode.Array(RewriteApplications(a.Elem)),
            TypeNode.ByRef b => new TypeNode.ByRef(RewriteApplications(b.Of)),
            TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, RewriteApplications(fn.Ret),
                fn.Params.Select(RewriteApplications).ToArray(),
                fn.Recv == null ? null : RewriteApplications(fn.Recv), fn.Clr,
                fn.Ctx?.Select(RewriteApplications).ToArray()),
            _ => type,
        };
        TypeNode.Fqn RewriteFqn(TypeNode.Fqn f)
        {
            if (f.Args == null) return f;
            var args = f.Args.Select(RewriteApplications).ToArray();
            if (!permutations.TryGetValue(f.Name, out var permutation))
                return new TypeNode.Fqn(f.Name, args);
            if (args.Length != permutation.NewToOld.Length)
                throw new InvalidOperationException(
                    $"Kotlin lifted type application '{f.Name}' has {args.Length} arguments " +
                    $"[{string.Join(", ", args.Select(a => TypeJson.Write(a).ToJsonString()))}] but its declaration has " +
                    $"{permutation.NewToOld.Length}");
            return new TypeNode.Fqn(f.Name, permutation.NewToOld.Select(i => args[i]).ToArray());
        }
        foreach (var root in roots) RewriteTypes(root, RewriteApplications);
    }

    // Normalize explicit BIR ownership facts before ordinary lowering consumes positional generic information.
    // Local functions are intentionally absent here: kotc keeps them as lexical localFun declarations, and
    // LocalFunctionLowering later consumes their declaration ids without reconstructing ownership from a flat method.
    public static void PrepareOwnershipFacts(IReadOnlyList<JsonNode> roots)
    {
        var types = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var root in roots.OfType<JsonObject>())
        {
            if (root["types"] is JsonArray declarations)
                foreach (var type in declarations.OfType<JsonObject>())
                    if (Str(type["name"]) is string name) types[name] = type;
        }

        NormalizeOwnerCapturePrefixes(roots);

        void EnsureSyntheticOwnerCapture(JsonObject node)
        {
            if (node["synthClass"] is not JsonObject synth
                || Str(synth["semanticOwner"]) is not string owner
                || !types.TryGetValue(owner, out var ownerType)
                || ownerType["typeParams"] is not JsonArray ownerParams
                || ownerParams.Count == 0)
                return;

            var typeArgs = node["typeArgs"] as JsonArray ?? new JsonArray();
            var typeParams = synth["typeParams"] as JsonArray ?? new JsonArray();
            if (typeArgs.Count != typeParams.Count)
                throw new InvalidOperationException(
                    $"synthetic type '{Str(synth["name"])}' has {typeParams.Count} parameters but " +
                    $"{typeArgs.Count} construction arguments");

            static bool IsOwnerSlot(JsonNode arg, int slot) =>
                arg is JsonObject tv && Str(tv["t"]) == "tv" && Str(tv["scope"]) == "type"
                && tv["i"] is JsonValue index && index.TryGetValue<int>(out var value) && value == slot;

            var consumed = new HashSet<int>();
            var normalizedParams = new JsonArray();
            var normalizedArgs = new JsonArray();
            for (var slot = 0; slot < ownerParams.Count; slot++)
            {
                var existing = Enumerable.Range(0, typeArgs.Count)
                    .FirstOrDefault(index => !consumed.Contains(index) && IsOwnerSlot(typeArgs[index], slot), -1);
                // The semantic owner is the authority for its slot constraints. A synthesized/materialized closure
                // may already carry the slot under a dense placeholder name, but that representation must not erase
                // `T : ...` when the closure becomes a CLR nested type.
                normalizedParams.Add(ownerParams[slot]?.DeepClone());
                normalizedArgs.Add(TypeJson.Write(new TypeNode.Tv("type", slot)));
                if (existing >= 0) consumed.Add(existing);
            }
            for (var index = 0; index < typeArgs.Count; index++)
                if (!consumed.Contains(index))
                {
                    normalizedParams.Add(typeParams[index]?.DeepClone());
                    normalizedArgs.Add(typeArgs[index]?.DeepClone());
                }

            synth["typeParams"] = normalizedParams;
            synth["outerTypeParamCount"] = ownerParams.Count;
            node["typeArgs"] = normalizedArgs;
        }

        void Rewrite(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                // A synthesized closure/SAM physically nested under a generic semantic owner needs the owner's full
                // constrained slot prefix even when its Kotlin body does not otherwise mention T. Moving a local
                // function onto Owner<T> can introduce exactly such an owner use after kotc's free-type scan.
                EnsureSyntheticOwnerCapture(obj);
                foreach (var value in obj.Select(kv => kv.Value).ToList())
                    if (value != null) Rewrite(value);
            }
            else if (node is JsonArray array)
                foreach (var value in array)
                    if (value != null) Rewrite(value);
        }
        foreach (var root in roots) Rewrite(root);
    }

    // The early representation boundary for type applications. Kotlin IR/BIR orders an inner classifier's flattened
    // arguments [own..., outer...]; ECMA-335 orders a nested TypeSpec [outer..., own...]. Perform that projection once,
    // before any CLR-oriented pass substitutes a callee-relative type variable through a constructed owner.
    public static void ProjectInnerApplications(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var semanticInnerShape = new Dictionary<string, (int CapturedCount, string Owner)>(StringComparer.Ordinal);
        foreach (var root in roots.OfType<JsonObject>())
            if (root["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>())
                    if (type["outerTypeParamCount"] is JsonValue countValue
                        && countValue.TryGetValue<int>(out var count) && count > 0
                        && type["mods"] is JsonObject mods
                        && mods["inner"] is JsonValue innerValue
                        && innerValue.TryGetValue<bool>(out var isInner) && isInner
                        && Str(type["name"]) is string innerName)
                        semanticInnerShape[innerName] = (count, Str(type["semanticOwner"])
                            ?? throw new InvalidOperationException(
                                $"Kotlin inner type '{innerName}' has no semantic owner"));

        TypeNode Project(TypeNode type) => type switch
        {
            TypeNode.Fqn f => ProjectFqn(f),
            TypeNode.Nullable n => new TypeNode.Nullable(Project(n.Of)),
            TypeNode.Oblivious o => new TypeNode.Oblivious(Project(o.Of)),
            TypeNode.Array a => new TypeNode.Array(Project(a.Elem)),
            TypeNode.ByRef b => new TypeNode.ByRef(Project(b.Of)),
            TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, Project(fn.Ret),
                fn.Params.Select(Project).ToArray(), fn.Recv == null ? null : Project(fn.Recv), fn.Clr,
                fn.Ctx?.Select(Project).ToArray()),
            _ => type,
        };

        TypeNode.Fqn ProjectFqn(TypeNode.Fqn f)
        {
            if (f.Args == null) return f;
            var args = f.Args.Select(Project).ToArray();
            var found = semanticInnerShape.TryGetValue(f.Name, out var shape);
            if (!found && (refs?.TryInnerCapturedCount(f.Name, out var capturedCount) ?? false))
            {
                if (!refs.TryInnerSemanticOwner(f.Name, out var semanticOwner))
                    throw new InvalidOperationException(
                        $"referenced Kotlin inner type '{f.Name}' has no semantic owner fact");
                shape = (capturedCount, semanticOwner);
                found = true;
            }
            if (!found || shape.CapturedCount == 0) return new TypeNode.Fqn(f.Name, args);
            if (shape.CapturedCount > args.Length)
                throw new InvalidOperationException(
                    $"Kotlin inner application '{f.Name}' supplies {args.Length} type arguments but declares " +
                    $"{shape.CapturedCount} captured outer slots");
            var ownCount = args.Length - shape.CapturedCount;
            var ownerApplication = ProjectFqn(new TypeNode.Fqn(
                shape.Owner, args.Skip(ownCount).ToArray()));
            return new TypeNode.Fqn(f.Name, ownerApplication.Args.Concat(args.Take(ownCount)).ToArray());
        }

        void Rewrite(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var value = obj[key];
                    if (value == null) continue;
                    if (TypeJson.IsType(value)) obj[key] = TypeJson.Write(Project(TypeJson.Read(value)));
                    else Rewrite(value);
                }
            }
            else if (node is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    var value = array[i];
                    if (value == null) continue;
                    if (TypeJson.IsType(value)) array[i] = TypeJson.Write(Project(TypeJson.Read(value)));
                    else Rewrite(value);
                }
            }
        }

        foreach (var root in roots) Rewrite(root);
    }

    public static void ApplyAll(IReadOnlyList<JsonNode> roots)
    {
        var declaredTypes = new HashSet<string>(StringComparer.Ordinal);
        var fileClasses = new HashSet<string>(StringComparer.Ordinal);
        var declarations = new List<(JsonObject Type, string FileClass)>();
        var typesByName = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (var root in roots.OfType<JsonObject>())
        {
            if (Str(root["fileClass"]) is string fileClass) fileClasses.Add(fileClass);
            if (root["types"] is not JsonArray types) continue;
            foreach (var type in types.OfType<JsonObject>())
            {
                if (Str(type["name"]) is string name)
                {
                    declaredTypes.Add(name);
                    typesByName[name] = type;
                }
                declarations.Add((type, Str(root["fileClass"])));
            }
        }

        // Compiler-generated methods can be projected by the frontend into the file method list even though their
        // Kotlin declaration owner is a class. Move only methods carrying that explicit BIR fact; never infer ownership
        // from names or bodies. This must precede TypeDef nesting so a generic owner supplies the method body's frame.
        foreach (var root in roots.OfType<JsonObject>())
        {
            var fileClass = Str(root["fileClass"]);
            if (root["methods"] is not JsonArray methods) continue;
            for (var index = methods.Count - 1; index >= 0; index--)
            {
                if (methods[index] is not JsonObject method
                    || Str(method["semanticOwner"]) is not string owner)
                    continue;
                method.Remove("semanticOwner");
                if (owner == fileClass) continue;
                if (!typesByName.TryGetValue(owner, out var ownerType))
                    throw new InvalidOperationException(
                        $"Kotlin method '{Str(method["name"])}' has missing semantic owner '{owner}' in this emission unit");
                methods.RemoveAt(index);
                var ownerMethods = ownerType["methods"] as JsonArray;
                if (ownerMethods == null) ownerType["methods"] = ownerMethods = new JsonArray();
                ownerMethods.Add(method);
            }
        }

        foreach (var (type, fileClass) in declarations)
        {
            if (Str(type["semanticOwner"]) is string owner)
            {
                var name = Str(type["name"]) ?? "<unnamed>";
                if (owner == name)
                    throw new InvalidOperationException($"Kotlin type '{name}' cannot own itself");
                if (!declaredTypes.Contains(owner) && !fileClasses.Contains(owner))
                    throw new InvalidOperationException(
                        $"Kotlin type '{name}' has missing semantic owner '{owner}' in this emission unit");
                type["nestedIn"] = owner;
                type.Remove("semanticOwner");
            }

            if (type["outerTypeParamCount"] is not JsonValue countValue
                || !countValue.TryGetValue<int>(out var count))
                continue;
            type.Remove("outerTypeParamOffset");
            type.Remove("outerTypeParamCount");
            if (count == 0) continue;
            var typeParams = type["typeParams"] as JsonArray;
            if (typeParams == null || count > typeParams.Count)
                throw new InvalidOperationException(
                    $"Kotlin inner type '{Str(type["name"])}' declares outerTypeParamCount={count} " +
                    $"but has only {typeParams?.Count ?? 0} type parameter declarations");
            if (type["nestedIn"] == null)
                throw new InvalidOperationException(
                    $"Kotlin inner type '{Str(type["name"])}' has captured outer parameters but no CLR owner");

            var captured = new JsonArray();
            var usedNames = typeParams
                .Select(parameter => parameter is JsonObject declaration
                    ? Str(declaration["name"])
                    : Str(parameter))
                .Where(name => name != null)
                .ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var parameter = typeParams[i]?.DeepClone()
                    ?? throw new InvalidOperationException(
                        $"Kotlin inner type '{Str(type["name"])}' has a missing captured type parameter at slot {i}");
                // CLR generic parameter names share one namespace across the flattened enclosing+declared slots.
                // Kotlin permits `Outer<T>.Inner<T>`, so retain slot identity/constraints but give captures a unique,
                // compiler-owned physical name. dll2klib drops these leading slots via KotlinInnerAttribute.
                var physicalName = $"dotkt$outer{i}";
                while (!usedNames.Add(physicalName)) physicalName += "$";
                if (parameter is JsonObject declaration) declaration["name"] = physicalName;
                else parameter = JsonValue.Create(physicalName);
                captured.Add(parameter);
            }
            for (var i = 0; i < count; i++)
                typeParams.RemoveAt(0);
            type["capturedTypeParams"] = captured;
            if (typeParams.Count == 0) type.Remove("typeParams");
        }

        // Ref builds intentionally skip suspend lowering, so its bir2cir-internal hand-off on a materialized local
        // suspend declaration has no consumer there. LocalFunctionLowering places such declarations only in their
        // selected source TypeDef's direct method list; clean that exact representation slot, not arbitrary bodies.
        foreach (var (type, _) in declarations)
            if (type["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>())
                    method.Remove("lexicalOwnerTypeParamCount");

    }
}
