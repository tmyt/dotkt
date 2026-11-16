using System.Globalization;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// CLR P/INVOKE DECLARATIONS — consume Kotlin's `external` declaration fact together with the exact external
// DllImportAttribute application and normalize them into one physical CIR import descriptor. DllImportAttribute is a
// pseudo-custom attribute: it must not survive beside the descriptor as an ordinary attribute blob. ilemit receives
// every MethodDef/ImplMap decision explicitly and only writes it through Reflection.Emit's equivalent metadata API.
static partial class PInvokeLowering
{
    const string DllImport = "System.Runtime.InteropServices.DllImportAttribute";

    static readonly HashSet<string> ScalarTypes = new(StringComparer.Ordinal)
    {
        "System.SByte", "System.Byte", "System.Int16", "System.UInt16",
        "System.Int32", "System.UInt32", "System.Int64", "System.UInt64", "System.Single", "System.Double",
        "System.IntPtr", "System.UIntPtr",
    };

    public static void Apply(BirFileDeclaration root, ReferenceMetadataIndex refs, IReadOnlySet<string> localEnums)
    {
        foreach (var method in root.Methods.OrEmpty()) ApplyMethod(method, refs, localEnums);
        foreach (var type in root.Types.OrEmpty())
            foreach (var method in Methods(type)) ApplyMethod(method, refs, localEnums);
    }

    // Owning declarations are flat; NestedIn records physical nesting. Do not
    // enter executable metadata payloads or body-local declaration templates.
    static BirFunctionDeclaration[] Methods(IBirTypeDeclaration type) => type switch {
        BirClassDefinition value => value.Methods.OrEmpty(),
        BirInterfaceDefinition value => value.Methods.OrEmpty(),
        BirEnumDefinition value => value.Methods.OrEmpty(),
        _ => throw new InvalidOperationException("Unaccounted P/Invoke declaration owner: " + type.GetType().Name),
    };

    static string StringConstant(BirFunctionDeclaration method, IBirAttributeValue node, string position)
    {
        if (node is BirConstant { Value.HasValue: true } constant && constant.Value.Value is BirLiteral.TextLiteral text)
            return text.Value;
        throw Error(method, $"{position} must be a constant String");
    }

    static bool BooleanConstant(BirFunctionDeclaration method, IBirAttributeValue node, string position)
    {
        if (node is BirConstant { Value.HasValue: true } constant && constant.Value.Value is BirLiteral.BooleanLiteral boolean)
            return boolean.Value;
        throw Error(method, $"{position} must be a constant Boolean");
    }

    static int EnumConstant(BirFunctionDeclaration method, IBirAttributeValue node, string position)
    {
        if (node is BirEnumValue value && value.PhysicalValue.HasValue &&
            int.TryParse(value.PhysicalValue.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            return result;
        throw Error(method, $"{position} must be a resolved CLR enum constant");
    }

    static InvalidOperationException Error(BirFunctionDeclaration method, string message) =>
        new($"{FieldLegality.PosPrefix(method.Pos.OrDefault()?.F.OrDefault(),
            method.Pos.HasValue && method.Pos.Value.L.HasValue ? method.Pos.Value.L.Value : (int?)null)}P/Invoke {message}");

    static void ApplyMethod(BirFunctionDeclaration method, ReferenceMetadataIndex refs, IReadOnlySet<string> localEnums)
    {
        var external = method.Mods.OrDefault()?.External.OrDefault() == true;
        var attrs = method.Attrs.OrEmpty();
        var imports = attrs.Where(attr => attr.Attr.OrDefault() is TypeNode.Fqn { Name: DllImport }).ToArray();
        if (!external && imports.Length == 0) return;

        var name = method.Name.OrDefault() ?? "<unnamed>";
        if (!external)
            throw Error(method, $"'{name}' carries DllImportAttribute without the Kotlin external declaration fact");
        if (imports.Length != 1)
            throw Error(method, $"external method '{name}' requires exactly one DllImportAttribute application");
        if (!method.Static.OrDefault())
            throw Error(method, $"'{name}' must be static");
        if (method.TypeParams.OrEmpty().Length != 0)
            throw Error(method, $"'{name}' must not be generic");
        if (method.Abstract.OrDefault() || method.Virtual.OrDefault() || method.Override.OrDefault())
            throw Error(method, $"'{name}' must not occupy a managed virtual slot");
        ValidateSignature(method, name, refs, localEnums);

        var import = imports[0];
        var ctorRef = import.MemberRef.OrDefault();
        if (!import.AttrExternal.OrDefault() || ctorRef?.Kind.OrDefault() != "ctor"
            || ctorRef.DeclaringType.OrDefault() is not TypeNode.Fqn { Name: DllImport })
            throw Error(method, $"'{name}' does not carry the exact external DllImportAttribute constructor identity");
        if (!import.Args.HasValue || import.Args.Value.Length != 1)
            throw Error(method, $"'{name}' requires the DllImportAttribute(String) constructor");

        var module = StringConstant(method, import.Args.Value[0], "library name");
        if (module.Length == 0)
            throw Error(method, $"'{name}' has an empty native library name");
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

        foreach (var named in import.NamedArgs.OrEmpty())
            {
                var argumentName = named.Name.OrDefault()
                    ?? throw Error(method, $"'{name}' has an unnamed DllImport argument");
                if (named.Kind.OrDefault() != "field")
                    throw Error(method, $"'{name}' DllImport argument '{argumentName}' is not a CLR field");
                if (!seen.Add(argumentName))
                    throw Error(method, $"'{name}' repeats DllImport argument '{argumentName}'");
                var value = named.Value.OrDefault();
                switch (argumentName)
                {
                    case "EntryPoint": entryPoint = StringConstant(method, value, "EntryPoint"); break;
                    case "CallingConvention":
                        callingConvention = EnumConstant(method, value, "CallingConvention") switch
                        {
                            1 => "winapi", 2 => "cdecl", 3 => "stdcall", 4 => "thiscall", 5 => "fastcall",
                            var v => throw Error(method, $"'{name}' has unsupported CallingConvention value {v}"),
                        };
                        break;
                    case "CharSet":
                        charSet = EnumConstant(method, value, "CharSet") switch
                        {
                            1 => "none", 2 => "ansi", 3 => "unicode", 4 => "auto",
                            var v => throw Error(method, $"'{name}' has unsupported CharSet value {v}"),
                        };
                        break;
                    case "ExactSpelling": exactSpelling = BooleanConstant(method, value, "ExactSpelling"); break;
                    case "SetLastError": setLastError = BooleanConstant(method, value, "SetLastError"); break;
                    case "PreserveSig": preserveSig = BooleanConstant(method, value, "PreserveSig"); break;
                    case "BestFitMapping": bestFitMapping = BooleanConstant(method, value, "BestFitMapping"); break;
                    case "ThrowOnUnmappableChar":
                        throwOnUnmappableChar = BooleanConstant(method, value, "ThrowOnUnmappableChar");
                        break;
                    default:
                        throw Error(method, $"'{name}' has unsupported DllImport argument '{argumentName}'");
                }
                if (argumentName != "CallingConvention" && (argumentName != "CharSet" || charSet != "none"))
                    pseudoFields.Add(argumentName);
            }

        method.Pinvoke = new BirPInvoke
        {
            Module = module,
            EntryPoint = entryPoint,
            CallingConvention = callingConvention,
            CallingConventionType = new TypeNode.Fqn("System.Runtime.InteropServices.CallingConvention"),
            CharSet = charSet,
            CharSetType = new TypeNode.Fqn("System.Runtime.InteropServices.CharSet"),
            ExactSpelling = exactSpelling,
            SetLastError = setLastError,
            PreserveSig = preserveSig,
            BestFitMapping = bestFitMapping,
            ThrowOnUnmappableChar = throwOnUnmappableChar,
            PseudoFields = pseudoFields.ToArray(),
            // Reflection.Emit exposes DllImport as a pseudo-custom-attribute API. The exact constructor operand is
            // already resolved by bir2cir; ilemit consumes it only as that API's metadata handle, never as an emitted
            // CustomAttribute row or a member-selection request.
            AttributeCtorRef = (BirMemberReference)ctorRef.DeepClone(),
        };
        method.Extern = true;
        method.Body = Array.Empty<IBirStatement>();
        if (method.Mods.HasValue)
        {
            var mods = method.Mods.Value;
            mods.External = default;
            if (mods == new BirModifiers()) method.Mods = default;
        }
        method.Attrs = attrs.Where(attribute => !ReferenceEquals(attribute, import)).ToArray();
    }

    static void ValidateSignature(
        BirFunctionDeclaration method,
        string name,
        ReferenceMetadataIndex refs,
        IReadOnlySet<string> localEnums)
    {
        if (!method.Ret.HasValue)
            throw Error(method, $"'{name}' has no return type");
        var returnType = method.Ret.Value;
        if (returnType is not TypeNode.Fqn { Name: "System.Void" or "void" or "kotlin.Unit" } &&
            !SupportedScalar(returnType, refs, localEnums))
            throw Error(method,
                $"'{name}' return type is outside the supported blittable primitive, enum, IntPtr, and UIntPtr subset");

        foreach (var parameter in method.Params.OrEmpty())
        {
            if (!parameter.Type.HasValue)
                throw Error(method, $"'{name}' has a parameter with no type");
            var type = parameter.Type.Value;
            if (type is TypeNode.ByRef byRef) type = byRef.Of;
            if (!SupportedScalar(type, refs, localEnums))
                throw Error(method,
                    $"'{name}' parameter '{parameter.Name.OrDefault() ?? "<unnamed>"}' is outside " +
                    "the supported blittable primitive, enum, IntPtr, UIntPtr, and ClrRef subset");
        }
    }

    static bool SupportedScalar(
        TypeNode type,
        ReferenceMetadataIndex refs,
        IReadOnlySet<string> localEnums) =>
        type is TypeNode.Fqn fqn && fqn.Args is null &&
        (ScalarTypes.Contains(fqn.Name) || localEnums.Contains(fqn.Name) || refs.IsEnumType(fqn));
}
