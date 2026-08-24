using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using DotKt.Klib.Metadata;

if (args.Length == 4 && args[0] == "--verify")
{
    VerifyDelegateShape(args[1], args[2]);
    VerifyDelegateShape(args[1], args[3]);
    return;
}

if (args.Length != 3 ||
    !int.TryParse(args[1], out var definitionCount) || definitionCount < 1 ||
    !int.TryParse(args[2], out var namespaceCount) || namespaceCount < 1)
    throw new ArgumentException(
        "usage: Generator <output-directory> <external-type-count> <consumer-namespace-count>");

var outputDirectory = Path.GetFullPath(args[0]);
Directory.CreateDirectory(outputDirectory);
var stem = $"Synthetic{definitionCount}N{namespaceCount}";

var externalAssembly = new PersistedAssemblyBuilder(
    new AssemblyName(stem + ".External"), typeof(object).Assembly);
var externalModule = externalAssembly.DefineDynamicModule(stem + ".External");
for (var i = 0; i < definitionCount; i++)
{
    var type = externalModule.DefineType(
        $"External.Types.Type{i:D6}",
        TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);
    type.DefineDefaultConstructor(MethodAttributes.Public);
    type.CreateType();
}
var delegateBuilder = externalModule.DefineType(
    "External.ProbeDelegate",
    TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
    typeof(MulticastDelegate));
var delegateConstructor = delegateBuilder.DefineConstructor(
    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName,
    CallingConventions.Standard,
    [typeof(object), typeof(IntPtr)]);
delegateConstructor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
var invoke = delegateBuilder.DefineMethod(
    "Invoke",
    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot |
    MethodAttributes.Virtual,
    CallingConventions.Standard,
    typeof(int),
    [typeof(int)]);
invoke.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
var delegateType = delegateBuilder.CreateType();
var contractBuilder = externalModule.DefineType(
    "External.IContract",
    TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
contractBuilder.DefineMethod(
    "Apply",
    MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.HideBySig |
    MethodAttributes.NewSlot | MethodAttributes.Virtual,
    CallingConventions.Standard,
    typeof(int),
    [delegateType]);
var contractType = contractBuilder.CreateType();
var contractMethod = contractType.GetMethod("Apply")
    ?? throw new InvalidOperationException("generated interface method missing");
var externalPath = Path.Combine(outputDirectory, stem + ".External.dll");
externalAssembly.Save(externalPath);

var consumerAssembly = new PersistedAssemblyBuilder(
    new AssemblyName(stem + ".Consumer"), typeof(object).Assembly);
var consumerModule = consumerAssembly.DefineDynamicModule(stem + ".Consumer");
for (var i = 0; i < namespaceCount; i++)
{
    var type = consumerModule.DefineType(
        $"Consumer.N{i:D6}.UseDelegate",
        TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);
    type.DefineDefaultConstructor(MethodAttributes.Public);
    type.AddInterfaceImplementation(contractType);
    var apply = type.DefineMethod(
        "Apply",
        MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.HideBySig |
        MethodAttributes.NewSlot | MethodAttributes.Virtual,
        CallingConventions.Standard,
        typeof(int),
        [delegateType]);
    var applyBody = apply.GetILGenerator();
    applyBody.Emit(OpCodes.Ldc_I4_0);
    applyBody.Emit(OpCodes.Ret);
    type.DefineMethodOverride(apply, contractMethod);
    var use = type.DefineMethod(
        "Use",
        MethodAttributes.Public | MethodAttributes.Static,
        CallingConventions.Standard,
        typeof(int),
        [delegateType]);
    var body = use.GetILGenerator();
    body.Emit(OpCodes.Ldc_I4_0);
    body.Emit(OpCodes.Ret);
    type.CreateType();
}
var consumerPath = Path.Combine(outputDirectory, stem + ".Consumer.dll");
consumerAssembly.Save(consumerPath);

static void VerifyDelegateShape(string path, string className)
{
    using var archive = ZipFile.OpenRead(path);
    foreach (var entry in archive.Entries.Where(entry =>
                 entry.FullName.EndsWith(".knm", StringComparison.Ordinal)))
    {
        using var stream = entry.Open();
        var fragment = PackageFragment.Parser.ParseFrom(stream);
        var declaration = fragment.Class.SingleOrDefault(candidate =>
            QualifiedName(fragment, candidate.FqName) == className);
        if (declaration is null) continue;
        if (!declaration.Supertype.Any(type =>
                type.HasClassName && QualifiedName(fragment, type.ClassName) == "External.IContract"))
            throw new InvalidDataException($"{className} does not expose External.IContract");
        foreach (var functionName in new[] { "Apply", "Use" })
        {
            var functions = declaration.Function.Where(candidate =>
                String(fragment, candidate.Name) == functionName).ToArray();
            if (functions.Length == 0)
                throw new InvalidDataException($"{className}.{functionName} is missing");
            foreach (var function in functions)
            {
                RequireType(fragment, function.ReturnType, "kotlin.Int");
                if (function.ValueParameter.Count != 1)
                    throw new InvalidDataException($"{className}.{functionName} must have one parameter");
                var parameter = function.ValueParameter[0].Type;
                RequireType(fragment, parameter, "kotlin.Function1");
                if (parameter.Argument.Count != 2)
                    throw new InvalidDataException(
                        $"{className}.{functionName} delegate must have two function arguments");
                RequireType(fragment, parameter.Argument[0].Type, "kotlin.Int");
                RequireType(fragment, parameter.Argument[1].Type, "kotlin.Int");
            }
        }
        return;
    }
    throw new InvalidDataException($"KLIB class '{className}' not found");
}

static void RequireType(PackageFragment fragment, DotKt.Klib.Metadata.Type type, string expected)
{
    var actual = type.HasClassName ? QualifiedName(fragment, type.ClassName) : "<non-classifier>";
    if (actual != expected)
        throw new InvalidDataException($"KLIB type '{actual}' is not '{expected}'");
}

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

static string String(PackageFragment fragment, int id) => fragment.Strings.String[id];
