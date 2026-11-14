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
        var sourceName = NestedSourceNames(roots.OfType<JsonObject>()
            .SelectMany(root => (root["types"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            .Select(type => (Str(type["name"]), Str(type["semanticOwner"]))));
        string Encode(TypeNode type) => CaptureNestedSourceType(type, sourceName, refs) is { } captured
            ? TypeNode.ToJson(captured) : null;

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

        TypeNode Project(TypeNode type) => ProjectInnerApplication(type, applicationOrder);

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
