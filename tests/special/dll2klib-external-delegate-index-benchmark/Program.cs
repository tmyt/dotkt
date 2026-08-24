using System.Reflection;
using System.Reflection.Emit;

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
