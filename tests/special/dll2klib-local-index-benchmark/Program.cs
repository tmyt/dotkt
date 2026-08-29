using System.Reflection;
using System.Reflection.Emit;

if (args.Length == 5 && args[0] == "--batch")
{
    if (!int.TryParse(args[2], out var assemblyCount) || assemblyCount < 1 ||
        !int.TryParse(args[3], out var typeCount) || typeCount < 1 ||
        !int.TryParse(args[4], out var batchNamespaceCount) ||
        batchNamespaceCount < 1 || batchNamespaceCount > typeCount)
        throw Usage();

    var directory = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(directory);
    for (var i = 0; i < assemblyCount; i++)
        Generate(Path.Combine(directory, $"Batch{i:D3}.dll"), typeCount, batchNamespaceCount);
    return;
}

if (args.Length != 3 ||
    !int.TryParse(args[1], out var count) || count < 1 ||
    !int.TryParse(args[2], out var namespaceCount) || namespaceCount < 1 || namespaceCount > count)
    throw Usage();

var output = Path.GetFullPath(args[0]);
Generate(output, count, namespaceCount);

static void Generate(string output, int count, int namespaceCount)
{
    var assemblyName = Path.GetFileNameWithoutExtension(output);
    var assembly = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
    var module = assembly.DefineDynamicModule(assemblyName);
    for (var i = 0; i < count; i++)
    {
        var type = module.DefineType(
            $"Synthetic.N{i % namespaceCount:D6}.Type{i:D6}",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);
        type.DefineDefaultConstructor(MethodAttributes.Public);
        type.CreateType();
    }
    assembly.Save(output);
}

static ArgumentException Usage() => new(
    "usage: Generator <output.dll> <public-type-count> <visible-namespace-count> | " +
    "Generator --batch <output-directory> <assembly-count> <public-type-count> <visible-namespace-count>");
