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
    static string Str(JsonNode node) => (node as JsonValue)?.GetValue<string>();

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
