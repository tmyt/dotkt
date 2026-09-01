using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DotKt.Klib.Metadata;

if (args.Length != 2)
    throw new ArgumentException("usage: PInvokeInspector <producer.dll> <producer.klib>");

VerifyDll(args[0]);
VerifyKlib(args[1]);
Console.WriteLine("P/Invoke MethodImport + dll2klib metadata: OK");

static void VerifyDll(string path)
{
    var expected = new Dictionary<string, (int Import, int Impl)>(StringComparer.Ordinal)
    {
        ["add"] = (0x0240, 0x0080),
        ["increment"] = (0x0100, 0x0080),
        ["none"] = (0x0100, 0x0080),
        ["ansi"] = (0x0102, 0x0080),
        ["auto"] = (0x0106, 0x0080),
        ["options"] = (0x2265, 0x0000),
    };
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var actual = new Dictionary<string, (int Import, int Impl)>(StringComparer.Ordinal);
    foreach (var handle in metadata.MethodDefinitions)
    {
        var method = metadata.GetMethodDefinition(handle);
        var import = method.GetImport();
        if (import.Module.IsNil) continue;
        var name = metadata.GetString(method.Name);
        Require((method.Attributes & (MethodAttributes.Static | MethodAttributes.PinvokeImpl)) ==
                (MethodAttributes.Static | MethodAttributes.PinvokeImpl),
            $"{name} is not a static PinvokeImpl MethodDef");
        Require(method.RelativeVirtualAddress == 0, $"{name} unexpectedly has a managed method body");
        Require(metadata.GetString(metadata.GetModuleReference(import.Module).Name) == "dotkt_pinvoke_probe",
            $"{name} has the wrong native module");
        actual[name] = ((int)import.Attributes, (int)method.ImplAttributes);
    }
    Require(actual.Count == expected.Count, $"expected {expected.Count} imports, found {actual.Count}");
    foreach (var (name, flags) in expected)
        Require(actual.GetValueOrDefault(name) == flags,
            $"{name} flags are import=0x{actual.GetValueOrDefault(name).Import:x4}, " +
            $"impl=0x{actual.GetValueOrDefault(name).Impl:x4}; expected 0x{flags.Import:x4}/0x{flags.Impl:x4}");
}

static void VerifyKlib(string path)
{
    using var archive = ZipFile.OpenRead(path);
    var fragment = archive.Entries
        .Where(entry => entry.FullName.EndsWith(".knm", StringComparison.Ordinal))
        .Select(entry =>
        {
            using var stream = entry.Open();
            return PackageFragment.Parser.ParseFrom(stream);
        })
        .Single(candidate => candidate.FqName == "");

    foreach (var name in new[] { "add", "increment", "none", "ansi", "auto", "options" })
    {
        var function = fragment.Package.Function.Single(candidate => String(fragment, candidate.Name) == name);
        Require((function.Flags & (1 << 12)) != 0, $"{name} lost Kotlin IS_EXTERNAL");
        var import = function.FunctionAnnotation.Single(annotation =>
            QualifiedName(fragment, annotation.Id) == "System.Runtime.InteropServices.DllImportAttribute");
        var arguments = import.Argument.ToDictionary(
            argument => String(fragment, argument.NameId),
            argument => argument.Value,
            StringComparer.Ordinal);
        Require(StringValue(fragment, arguments["dllName"]) == "dotkt_pinvoke_probe",
            $"{name} lost its DllImport library name");
        Require(EnumValue(fragment, arguments["CallingConvention"]) ==
                "System.Runtime.InteropServices.CallingConvention.Winapi" || name is "add" or "options",
            $"{name} did not reconstruct the Winapi default");
    }

    var add = ImportArguments(fragment, "add");
    Require(StringValue(fragment, add["EntryPoint"]) == "add_i32", "add lost EntryPoint");
    Require(EnumValue(fragment, add["CallingConvention"]) ==
            "System.Runtime.InteropServices.CallingConvention.Cdecl", "add lost Cdecl");
    Require(BooleanValue(add["SetLastError"]), "add lost SetLastError=true");

    var none = ImportArguments(fragment, "none");
    Require(!none.ContainsKey("CharSet"), "CharSet.None should reconstruct through the annotation default");
    var ansi = ImportArguments(fragment, "ansi");
    Require(EnumValue(fragment, ansi["CharSet"]) ==
            "System.Runtime.InteropServices.CharSet.Ansi", "ansi lost CharSet.Ansi");
    var auto = ImportArguments(fragment, "auto");
    Require(EnumValue(fragment, auto["CharSet"]) ==
            "System.Runtime.InteropServices.CharSet.Auto", "auto lost CharSet.Auto");

    var options = ImportArguments(fragment, "options");
    Require(!BooleanValue(options["PreserveSig"]), "options lost PreserveSig=false");
    Require(!BooleanValue(options["BestFitMapping"]), "options lost BestFitMapping=false");
    Require(!BooleanValue(options["ThrowOnUnmappableChar"]), "options lost ThrowOnUnmappableChar=false");

    Dictionary<string, Annotation.Types.Argument.Types.Value> ImportArguments(
        PackageFragment owner, string functionName)
    {
        var function = owner.Package.Function.Single(candidate => String(owner, candidate.Name) == functionName);
        var annotation = function.FunctionAnnotation.Single(candidate =>
            QualifiedName(owner, candidate.Id) == "System.Runtime.InteropServices.DllImportAttribute");
        return annotation.Argument.ToDictionary(
            argument => String(owner, argument.NameId), argument => argument.Value, StringComparer.Ordinal);
    }
}

static bool BooleanValue(Annotation.Types.Argument.Types.Value value) => value.IntValue != 0;
static string StringValue(PackageFragment fragment, Annotation.Types.Argument.Types.Value value) =>
    String(fragment, value.StringValue);
static string EnumValue(PackageFragment fragment, Annotation.Types.Argument.Types.Value value) =>
    QualifiedName(fragment, value.ClassId) + "." + String(fragment, value.EnumValueId);
static string String(PackageFragment fragment, int id) => fragment.Strings.String[id];
static string QualifiedName(PackageFragment fragment, int id)
{
    var parts = new Stack<string>();
    while (id >= 0)
    {
        var item = fragment.QualifiedNames.QualifiedName[id];
        parts.Push(String(fragment, item.ShortName));
        id = item.ParentQualifiedName;
    }
    return string.Join('.', parts);
}
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}
