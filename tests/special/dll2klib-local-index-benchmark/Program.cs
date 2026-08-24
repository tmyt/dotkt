using System.Reflection;
using System.Reflection.Emit;

if (args.Length != 3 ||
    !int.TryParse(args[1], out var count) || count < 1 ||
    !int.TryParse(args[2], out var namespaceCount) || namespaceCount < 1 || namespaceCount > count)
    throw new ArgumentException(
        "usage: Generator <output.dll> <public-type-count> <visible-namespace-count>");

var output = Path.GetFullPath(args[0]);
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
