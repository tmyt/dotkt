using System.Collections;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// Inspect the actual current importer on a real native declaration, not a
// second projection implementation or synthesized comparison metadata.
var tool = Assembly.LoadFrom(Path.GetFullPath(args[0]));
var snapshotType = tool.GetType("ReferenceMetadataSnapshot", true)!;
var inputs = new[] { typeof(object).Assembly.Location, typeof(NativeComparison.DualOrder).Assembly.Location,
    Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll") };
if (args.Length > 1) inputs = inputs.Append(Path.GetFullPath(args[1])).ToArray();
var snapshots = Array.CreateInstance(snapshotType, inputs.Length);
for (var i = 0; i < inputs.Length; i++) snapshots.SetValue(snapshotType.GetMethod("Open")!.Invoke(null, [inputs[i]])!, i);
object Discover(string name) => tool.GetType(name, true)!.GetMethod("Discover")!.Invoke(null, [snapshots])!;
var delegates = Discover("DelegateReferenceCatalog");
var companions = Discover("CompanionReferenceCatalog");
var inners = Discover("InnerReferenceCatalog");
using var publicTypes = (IDisposable)Discover("PublicTypeCatalog");
var views = Discover("SourceInterfaceViews");
var cases = new List<(int Input, string Name, int Expected)> {
    (0, "DateTime", 1), (0, "IComparable`1", 0),
    (1, "NativeOrder", 1), (1, "InheritedOrder", 1), (1, "ExplicitOrder", 1),
    (1, "InheritedExplicitOrder", 1), (1, "DualOrder", 2), (1, "PermutedOrder`2", 1), (1, "MutableOrder", 1),
};
if (inputs.Length > 3) cases.AddRange(cases.Where(test => test.Input == 1).Select(test => (3, test.Name, test.Expected)).ToArray());
var runtimeTargets = new Dictionary<string, string[]>();
foreach (var (inputIndex, typeName, expected) in cases) {
    var input = inputs[inputIndex];
    var reader = (MetadataReader)snapshotType.GetProperty("Reader")!.GetValue(snapshots.GetValue(inputIndex))!;
    var arities = tool.GetType("ArityNames", true)!.GetMethod("Create")!.Invoke(null, [reader, null])!;
    using var stream = File.OpenRead(input);
    using var pe = new PEReader(stream);
    var scannerType = tool.GetType("AssemblyScanner", true)!;
    using var scanner = (IDisposable)Activator.CreateInstance(scannerType,
        [input, pe, reader, arities, null, delegates, companions, inners, publicTypes, views])!;
    var names = Activator.CreateInstance(tool.GetType("NameTable", true)!)!;
    object ScannerField(string name) => scannerType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
    var decoder = Activator.CreateInstance(tool.GetType("SignatureDecoder", true)!,
        [reader, names, ScannerField("_attrs"), arities, delegates, companions, inners,
            ScannerField("_signatureSeeds"), ScannerField("_externalSignatureDecoders"), publicTypes, null])!;
    var handle = reader.TypeDefinitions.Single(h => reader.GetString(reader.GetTypeDefinition(h).Name) == typeName);
    var projected = scannerType.GetMethod("ReadClass", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(scanner, [handle, reader.GetTypeDefinition(handle), names, decoder, null, null])!;
    object Get(object obj, string property) => obj.GetType().GetProperty(property)!.GetValue(obj)!;
    object[] Items(object obj, string property) => ((IEnumerable)Get(obj, property)).Cast<object>().ToArray();
    string Text(object index) => (string)names.GetType().GetMethod("StringValue")!.Invoke(names, [index])!;
    string ClassName(object index) => (string)names.GetType().GetMethod("ClassName")!.Invoke(names, [index])!;
    var functions = Items(projected, "Function");
    var original = functions.Where(f => Text(Get(f, "Name")) == "CompareTo").ToArray();
    var operators = functions.Where(f => Text(Get(f, "Name")) == "compareTo" && ((int)Get(f, "Flags") & (1 << 8)) != 0).ToArray();
    if (typeName == "DateTime" && original.Length != 2) throw new Exception("DateTime native CompareTo overloads changed");
    if (operators.Length != expected) throw new Exception($"{typeName}: expected {expected} additional operators, got {operators.Length}");
    var owners = new List<string>();
    foreach (var view in operators) {
        if (((int)Get(view, "Flags") & 1) == 0) throw new Exception("Selected target annotation is invisible to the frontend");
        if ((((int)Get(view, "Flags") >> 4) & 3) != 0) throw new Exception("Source view invented a virtual/abstract slot");
        var annotation = Items(view, "FunctionAnnotation").Single(a => ClassName(Get(a, "Id")) == "kotlin.clr.ClrExternal");
        var facts = Items(annotation, "Argument").ToDictionary(a => Text(Get(a, "NameId")), a => Text(Get(Get(a, "Value"), "StringValue")));
        if (facts["memberName"] != "CompareTo") throw new Exception("Native declaration target changed");
        if (Items(view, "FunctionAnnotation").Any(a => ClassName(Get(a, "Id")) == "kotlin.clr.KotlinDeclarationIdentity"))
            throw new Exception("A source-only view was assigned a fabricated declaration identity");
        using var target = JsonDocument.Parse(facts["memberOwner"]);
        if (target.RootElement.GetProperty("name").GetString() != "System.IComparable`1") throw new Exception("Target lost its exact native owner");
        owners.Add(facts["memberOwner"]);
    }
    if (owners.Distinct().Count() != operators.Length) throw new Exception("Constructed contracts collapsed");
    if (typeName == "DualOrder" && (!owners.Any(o => o.Contains("System.String")) || !owners.Any(o => o.Contains("System.Int32"))))
        throw new Exception("Dual contract arguments were inferred or erased");
    if (typeName == "PermutedOrder`2" && !owners.Single().Contains("\"i\":1"))
        throw new Exception("Selected target lost the native declaring frame's second variable");
    if (inputIndex == 1) runtimeTargets.Add(typeName, owners.ToArray());
    if (inputIndex == 3 && !runtimeTargets[typeName].SequenceEqual(owners))
        throw new Exception("Reference assembly projection changed the selected declaration owner or frame");
    Console.WriteLine($"{typeName} ({(inputIndex == 3 ? "reference" : "runtime")}): original CompareTo={original.Length}, operator compareTo={operators.Length}; targets={string.Join(";", owners)}");
}
if (inputs.Length > 3) {
    Guid Mvid(int index) {
        var reader = (MetadataReader)snapshotType.GetProperty("Reader")!.GetValue(snapshots.GetValue(index))!;
        return reader.GetGuid(reader.GetModuleDefinition().Mvid);
    }
    if (Mvid(1) == Mvid(3)) throw new Exception("Reference/runtime proof did not use independent module identities");
    Console.WriteLine($"Reference/runtime modules differ: {Mvid(1)} vs {Mvid(3)}; selected owner/frame facts are identical");
}
foreach (IDisposable snapshot in snapshots) snapshot.Dispose();
Console.WriteLine("Native comparable projection: PASS");
