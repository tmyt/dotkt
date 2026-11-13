using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Kotlin declarations carry only their semantic lexical owner in BIR. This pass is the single place that selects
// the corresponding CLR TypeDef nesting and rewrites inner-class outer generic declarations into explicit physical
// capture slots. ilemit then emits nestedIn/capturedTypeParams one-to-one.
static partial class TypeOwnershipLowering
{
    // KotlinType metadata must retain the source classifier path and own-first inner arguments, before physical
    // capture projection permutes those arguments. The '+' path separator explicitly records a class segment;
    // the reader must not guess whether a dot separated a package or an enclosing class.
    public static void RecordNestedSourceTypes(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var definitions = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var type in roots.OfType<JsonObject>()
            .SelectMany(root => (root["types"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            .Where(type => Str(type["name"]) != null))
        {
            var name = Str(type["name"]);
            // Shared synthetic declarations can occur in several roots before module de-duplication.
            // This index owns only their lexical path; repeated declarations must agree on that owner.
            if (!definitions.TryAdd(name, type)
                && Str(definitions[name]["semanticOwner"]) != Str(type["semanticOwner"]))
                throw new InvalidOperationException($"conflicting Kotlin semantic owners for '{name}'");
        }
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        string SourceName(string name)
        {
            if (names.TryGetValue(name, out var known)) return known;
            if (!definitions.TryGetValue(name, out var type)
                || Str(type["semanticOwner"]) is not string owner || !definitions.ContainsKey(owner))
                return names[name] = name;
            if (!name.StartsWith(owner + ".", StringComparison.Ordinal))
                return names[name] = name;
            return names[name] = SourceName(owner) + "+" + name[(owner.Length + 1)..];
        }

        string Encode(TypeNode type)
        {
            var payload = TypeJson.Write(type);
            var hasNestedClassifier = false;
            void Rewrite(JsonNode node)
            {
                if (node is JsonObject obj)
                {
                    if (Str(obj["t"]) == "fqn" && Str(obj["name"]) is string name)
                    {
                        var sourceName = SourceName(name);
                        if (obj["classifier"] == null && sourceName != name) obj["name"] = sourceName;
                        // A CLR-exact nested reference does not need a KotlinType carrier unless its
                        // source arguments or the signature's physical representation require one.
                        hasNestedClassifier |= (obj["classifier"] != null && !name.Contains('+')) || sourceName != name
                            || (refs?.TryInnerCapturedCount(name, out var captured) == true && captured > 0);
                    }
                    foreach (var child in obj.Select(pair => pair.Value).Where(value => value != null).ToList())
                        Rewrite(child);
                }
                else if (node is JsonArray array)
                    foreach (var child in array.Where(value => value != null)) Rewrite(child);
            }
            Rewrite(payload);
            return hasNestedClassifier ? payload.ToJsonString() : null;
        }

        void Record(JsonObject declaration, string slot, string fact)
        {
            var source = Str(declaration[fact]) ?? Str(declaration[slot == "ret" ? "nullableGenericRet" : "nullableGeneric"]);
            var type = source == null ? TypeJson.Read(declaration[slot]) : TypeNode.Parse(source);
            if (type != null && Encode(type) is string encoded) declaration[fact] = encoded;
        }

        void Visit(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (obj["k"] == null && obj["name"] != null)
                {
                    if (obj["type"] != null) Record(obj, "type", "kotlinType");
                    if (obj["params"] is JsonArray && obj["ret"] != null) Record(obj, "ret", "retKotlinType");
                }
                foreach (var child in obj.Select(pair => pair.Value).Where(value => value != null).ToList()) Visit(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array.Where(value => value != null)) Visit(child);
        }
        foreach (var root in roots) Visit(root);
    }

    static string Str(JsonNode node) => (node as JsonValue)?.GetValue<string>();


    // The early representation boundary for type applications. Kotlin IR/BIR orders an inner classifier's flattened
    // arguments [own..., outer...]; ECMA-335 orders a nested TypeSpec [outer..., own...]. Perform that projection once,
    // before any CLR-oriented pass substitutes a callee-relative type variable through a constructed owner.
    public static void ProjectInnerApplications(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var applicationOrder = new InnerApplicationOrder(roots, refs);

        TypeNode Project(TypeNode type) => type switch
        {
            TypeNode.Fqn f => ProjectFqn(f),
            TypeNode.Nullable n => new TypeNode.Nullable(Project(n.Of)),
            TypeNode.Oblivious o => new TypeNode.Oblivious(Project(o.Of)),
            TypeNode.Projection p => new TypeNode.Projection(p.Variance, Project(p.Of)),
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
            var ordered = applicationOrder.DeclarationArguments(f with { Args = args });
            // Reordered inner arguments now describe the CLR frame; the source application was
            // recorded before this pass. Other classifiers still describe the same source head.
            return ReferenceEquals(args, ordered) ? f with { Args = args } : new TypeNode.Fqn(f.Name, ordered);
        }

        void Rewrite(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    // These facts still use source generic frames; their consumers close the recorded frame.
                    if (key is "overrides" or DeclarationIdentityBinding.SemanticSignatureKey) continue;
                    var value = obj[key];
                    if (value == null) continue;
                    if (TypeJson.IsType(value))
                    {
                        try { obj[key] = TypeJson.Write(Project(TypeJson.Read(value))); }
                        catch (InvalidOperationException error) {
                            throw new InvalidOperationException($"{error.Message} at {value.GetPath()}", error);
                        }
                    }
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
                type["nestedIn"] ??= owner;
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
                .Select(parameter => Str(parameter["name"]))
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
                parameter["name"] = physicalName;
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
