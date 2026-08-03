// Prints the KFunc`N / KAction`N types an assembly DEFINES (TypeDef rows), one per line, sorted.
// `strings` cannot answer this: a merely-REFERENCED type puts its name in the same #Strings heap, which is
// exactly the distinction #220 turns on — an app must reference the stdlib's canonical family, never define one.
// Run as a file-based app: `dotnet run <this file> <assembly.dll>`.
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: delegate-typedefs.cs <assembly.dll>");
    return 2;
}

using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
var md = pe.GetMetadataReader();
var defined = new List<string>();
foreach (var handle in md.TypeDefinitions)
{
    var def = md.GetTypeDefinition(handle);
    var name = md.GetString(def.Name);
    if (!name.StartsWith("KFunc`", StringComparison.Ordinal) && !name.StartsWith("KAction`", StringComparison.Ordinal))
        continue;
    var ns = md.GetString(def.Namespace);
    defined.Add(ns.Length == 0 ? name : ns + "." + name);
}
defined.Sort(StringComparer.Ordinal);
foreach (var name in defined) Console.WriteLine(name);
return 0;
