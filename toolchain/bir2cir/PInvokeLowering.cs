using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;

// CLR P/INVOKE DECLARATIONS — consume Kotlin's `external` declaration fact together with the exact external
// DllImportAttribute application and normalize them into one physical CIR import descriptor. DllImportAttribute is a
// pseudo-custom attribute: it must not survive beside the descriptor as an ordinary attribute blob. ilemit receives
// every MethodDef/ImplMap decision explicitly and only writes it through Reflection.Emit's equivalent metadata API.
static class PInvokeLowering
{
    const string DllImport = "System.Runtime.InteropServices.DllImportAttribute";

    static readonly HashSet<string> ScalarTypes = new(StringComparer.Ordinal)
    {
        "System.Boolean", "System.Char", "System.SByte", "System.Byte", "System.Int16", "System.UInt16",
        "System.Int32", "System.UInt32", "System.Int64", "System.UInt64", "System.Single", "System.Double",
        "System.IntPtr", "System.UIntPtr",
    };

    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        if (root is not JsonObject file) return;
        var localEnums = new HashSet<string>(StringComparer.Ordinal);
        CollectLocalEnums(file["types"] as JsonArray, localEnums);
        ApplyMethods(file["methods"] as JsonArray, refs, localEnums);
        ApplyTypes(file["types"] as JsonArray, refs, localEnums);
    }

    static void CollectLocalEnums(JsonArray types, HashSet<string> result)
    {
        if (types == null) return;
        foreach (var type in types.OfType<JsonObject>())
        {
            if (Mod(type, "enum") && Str(type["name"]) is string name) result.Add(name);
            CollectLocalEnums(type["types"] as JsonArray, result);
        }
    }

    static void ApplyTypes(JsonArray types, ReferenceMetadataIndex refs, IReadOnlySet<string> localEnums)
    {
        if (types == null) return;
        foreach (var node in types.OfType<JsonObject>())
        {
            ApplyMethods(node["methods"] as JsonArray, refs, localEnums);
            ApplyTypes(node["types"] as JsonArray, refs, localEnums);
        }
    }

    static void ApplyMethods(JsonArray methods, ReferenceMetadataIndex refs, IReadOnlySet<string> localEnums)
    {
        if (methods == null) return;
        foreach (var method in methods.OfType<JsonObject>()) ApplyMethod(method, refs, localEnums);
    }

    static bool Mod(JsonObject method, string name) =>
        method["mods"] is JsonObject mods && Bool(mods[name]);

    static bool Bool(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    static string StringConstant(JsonNode node, string position)
    {
        if (node is JsonObject obj && Str(obj["k"]) == "const" && obj["value"] is JsonValue value &&
            value.TryGetValue<string>(out var text) && text != null)
            return text;
        throw new InvalidOperationException($"bir2cir: P/Invoke {position} must be a constant String");
    }

    static bool BooleanConstant(JsonNode node, string position)
    {
        if (node is JsonObject obj && Str(obj["k"]) == "const" && obj["value"] is JsonValue value &&
            value.TryGetValue<bool>(out var result))
            return result;
        throw new InvalidOperationException($"bir2cir: P/Invoke {position} must be a constant Boolean");
    }

    static int EnumConstant(JsonNode node, string position)
    {
        if (node is JsonObject obj && Str(obj["k"]) == "enumValue" &&
            obj["physicalValue"] is JsonValue value && value.TryGetValue<string>(out var text) &&
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            return result;
        throw new InvalidOperationException($"bir2cir: P/Invoke {position} must be a resolved CLR enum constant");
    }

    static string Str(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static void ApplyMethod(JsonObject method, ReferenceMetadataIndex refs, IReadOnlySet<string> localEnums)
    {
        var external = Mod(method, "external");
        var attrs = method["attrs"] as JsonArray;
        var imports = attrs?.OfType<JsonObject>()
            .Where(attr => TypeJson.OwnerName(attr["attr"]) == DllImport)
            .ToList() ?? new List<JsonObject>();
        if (!external && imports.Count == 0) return;

        var name = Str(method["name"]) ?? "<unnamed>";
        if (!external)
            throw new InvalidOperationException(
                $"bir2cir: P/Invoke '{name}' carries DllImportAttribute without the Kotlin external declaration fact");
        if (imports.Count != 1)
            throw new InvalidOperationException(
                $"bir2cir: external method '{name}' requires exactly one DllImportAttribute application");
        if (!Bool(method["static"]))
            throw new InvalidOperationException($"bir2cir: P/Invoke '{name}' must be static");
        if (method["typeParams"] is JsonArray typeParameters && typeParameters.Count != 0)
            throw new InvalidOperationException($"bir2cir: P/Invoke '{name}' must not be generic");
        if (Bool(method["abstract"]) || Bool(method["virtual"]) || Bool(method["override"]))
            throw new InvalidOperationException($"bir2cir: P/Invoke '{name}' must not occupy a managed virtual slot");
        ValidateSignature(method, name, refs, localEnums);

        var import = imports[0];
        if (!Bool(import["attrExternal"]) || import["memberRef"] is not JsonObject ctorRef ||
            Str(ctorRef["kind"]) != "ctor" || TypeJson.OwnerName(ctorRef["declaringType"]) != DllImport)
            throw new InvalidOperationException(
                $"bir2cir: P/Invoke '{name}' does not carry the exact external DllImportAttribute constructor identity");
        if (import["args"] is not JsonArray fixedArguments || fixedArguments.Count != 1)
            throw new InvalidOperationException(
                $"bir2cir: P/Invoke '{name}' requires the DllImportAttribute(String) constructor");

        var module = StringConstant(fixedArguments[0], "library name");
        if (module.Length == 0)
            throw new InvalidOperationException($"bir2cir: P/Invoke '{name}' has an empty native library name");
        var entryPoint = name;
        var callingConvention = "winapi";
        var charSet = "none";
        var exactSpelling = false;
        var setLastError = false;
        var preserveSig = true;
        var bestFitMapping = false;
        var throwOnUnmappableChar = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Reflection.Emit's raw DllImport pseudo-attribute API does not materialize the CLR Winapi default when the
        // field is omitted, whereas a C# DllImport declaration does. Conversely, explicitly writing CharSet.None
        // through that API produces AutoChar instead of the physical no-charset bits. State the exact pseudo fields
        // ilemit must write after this physical normalization; source-level presence is not an output fact.
        var pseudoFields = new List<string> { "CallingConvention" };

        if (import["namedArgs"] is JsonArray namedArguments)
            foreach (var named in namedArguments.OfType<JsonObject>())
            {
                var argumentName = Str(named["name"])
                    ?? throw new InvalidOperationException($"bir2cir: P/Invoke '{name}' has an unnamed DllImport argument");
                if (Str(named["kind"]) != "field")
                    throw new InvalidOperationException(
                        $"bir2cir: P/Invoke '{name}' DllImport argument '{argumentName}' is not a CLR field");
                if (!seen.Add(argumentName))
                    throw new InvalidOperationException(
                        $"bir2cir: P/Invoke '{name}' repeats DllImport argument '{argumentName}'");
                var value = named["value"];
                switch (argumentName)
                {
                    case "EntryPoint": entryPoint = StringConstant(value, "EntryPoint"); break;
                    case "CallingConvention":
                        callingConvention = EnumConstant(value, "CallingConvention") switch
                        {
                            1 => "winapi", 2 => "cdecl", 3 => "stdcall", 4 => "thiscall", 5 => "fastcall",
                            var v => throw new InvalidOperationException(
                                $"bir2cir: P/Invoke '{name}' has unsupported CallingConvention value {v}"),
                        };
                        break;
                    case "CharSet":
                        charSet = EnumConstant(value, "CharSet") switch
                        {
                            1 => "none", 2 => "ansi", 3 => "unicode", 4 => "auto",
                            var v => throw new InvalidOperationException(
                                $"bir2cir: P/Invoke '{name}' has unsupported CharSet value {v}"),
                        };
                        break;
                    case "ExactSpelling": exactSpelling = BooleanConstant(value, "ExactSpelling"); break;
                    case "SetLastError": setLastError = BooleanConstant(value, "SetLastError"); break;
                    case "PreserveSig": preserveSig = BooleanConstant(value, "PreserveSig"); break;
                    case "BestFitMapping": bestFitMapping = BooleanConstant(value, "BestFitMapping"); break;
                    case "ThrowOnUnmappableChar":
                        throwOnUnmappableChar = BooleanConstant(value, "ThrowOnUnmappableChar");
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"bir2cir: P/Invoke '{name}' has unsupported DllImport argument '{argumentName}'");
                }
                if (argumentName != "CallingConvention" && (argumentName != "CharSet" || charSet != "none"))
                    pseudoFields.Add(argumentName);
            }

        method["pinvoke"] = new JsonObject
        {
            ["module"] = module,
            ["entryPoint"] = entryPoint,
            ["callingConvention"] = callingConvention,
            ["callingConventionType"] = TypeJson.Fqn("System.Runtime.InteropServices.CallingConvention"),
            ["charSet"] = charSet,
            ["charSetType"] = TypeJson.Fqn("System.Runtime.InteropServices.CharSet"),
            ["exactSpelling"] = exactSpelling,
            ["setLastError"] = setLastError,
            ["preserveSig"] = preserveSig,
            ["bestFitMapping"] = bestFitMapping,
            ["throwOnUnmappableChar"] = throwOnUnmappableChar,
            ["pseudoFields"] = new JsonArray(pseudoFields.Select(value => (JsonNode)value).ToArray()),
            // Reflection.Emit exposes DllImport as a pseudo-custom-attribute API. The exact constructor operand is
            // already resolved by bir2cir; ilemit consumes it only as that API's metadata handle, never as an emitted
            // CustomAttribute row or a member-selection request.
            ["attributeCtorRef"] = ctorRef.DeepClone(),
        };
        method["extern"] = true;
        method["body"] = new JsonArray();
        if (method["mods"] is JsonObject mods)
        {
            mods.Remove("external");
            if (mods.Count == 0) method.Remove("mods");
        }
        attrs.Remove(import);
    }

    static void ValidateSignature(
        JsonObject method,
        string name,
        ReferenceMetadataIndex refs,
        IReadOnlySet<string> localEnums)
    {
        if (method["ret"] is not JsonNode returnNode)
            throw new InvalidOperationException($"bir2cir: P/Invoke '{name}' has no return type");
        var returnType = TypeNode.Read(JsonDocument.Parse(returnNode.ToJsonString()).RootElement);
        if (returnType is not TypeNode.Fqn { Name: "System.Void" or "void" or "kotlin.Unit" } &&
            !SupportedScalar(returnType, refs, localEnums))
            throw new InvalidOperationException(
                $"bir2cir: P/Invoke '{name}' return type is outside the supported primitive, enum, IntPtr, and UIntPtr subset");

        if (method["params"] is not JsonArray parameters) return;
        foreach (var parameter in parameters.OfType<JsonObject>())
        {
            if (parameter["type"] is not JsonNode typeNode)
                throw new InvalidOperationException($"bir2cir: P/Invoke '{name}' has a parameter with no type");
            var type = TypeNode.Read(JsonDocument.Parse(typeNode.ToJsonString()).RootElement);
            if (type is TypeNode.ByRef byRef) type = byRef.Of;
            if (!SupportedScalar(type, refs, localEnums))
                throw new InvalidOperationException(
                    $"bir2cir: P/Invoke '{name}' parameter '{Str(parameter["name"]) ?? "<unnamed>"}' is outside " +
                    "the supported primitive, enum, IntPtr, UIntPtr, and ClrRef subset");
        }
    }

    static bool SupportedScalar(
        TypeNode type,
        ReferenceMetadataIndex refs,
        IReadOnlySet<string> localEnums) =>
        type is TypeNode.Fqn fqn && fqn.Args is null &&
        (ScalarTypes.Contains(fqn.Name) || localEnums.Contains(fqn.Name) || refs.IsNetEnum(fqn));
}
