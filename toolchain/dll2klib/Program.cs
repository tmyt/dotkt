using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DotKt.Bir;
using DotKt.Klib.Metadata;
using Google.Protobuf;
using KType = DotKt.Klib.Metadata.Type;

internal static class Program
{
    private const string ArityClashesEnvironment = "DOTKT_DLL2KLIB_ARITY_CLASHES";
    private const string DelegateCatalogEnvironment = "DOTKT_DLL2KLIB_DELEGATE_CATALOG";
    private const string CompanionCatalogEnvironment = "DOTKT_DLL2KLIB_COMPANION_CATALOG";
    private const string InnerCatalogEnvironment = "DOTKT_DLL2KLIB_INNER_CATALOG";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                var input = Path.GetFullPath(args[0]);
                if (IsStandardLibrary(input))
                {
                    Console.Error.WriteLine(
                        $"dll2klib: warning: ignored Kotlin standard library assembly '{Path.GetFileName(input)}'; " +
                        "use the frontend standard-library KLIB instead");
                    return 0;
                }
                // This two-path form is the batch launcher's worker protocol. A correct projection of an external
                // delegate TypeRef needs the complete resolved assembly universe; one input DLL cannot establish the
                // referenced TypeDef's identity or Invoke shape on its own. Refuse a human standalone invocation
                // instead of silently projecting such a delegate as an ordinary nominal class. The batch parent sets
                // both catalogs on every worker below.
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DelegateCatalogEnvironment)) ||
                    string.IsNullOrEmpty(Environment.GetEnvironmentVariable(CompanionCatalogEnvironment)) ||
                    string.IsNullOrEmpty(Environment.GetEnvironmentVariable(InnerCatalogEnvironment)))
                    throw new InvalidOperationException(
                        "direct worker mode requires the batch-provided resolved delegate, companion, and inner catalogs; " +
                        "use 'dll2klib --out <directory> @<references.rsp>' with the complete reference set");
                Convert(input, Path.GetFullPath(args[1]));
                return 0;
            }
            return await ConvertBatch(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dll2klib: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> ConvertBatch(string[] args)
    {
        string? outputDirectory = null;
        string? responseFile = null;
        var jobs = Environment.ProcessorCount;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length:
                    outputDirectory = Path.GetFullPath(args[++i]);
                    break;
                case "--jobs" when i + 1 < args.Length &&
                    int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                    parsed >= 0:
                    jobs = parsed;
                    break;
                default:
                    if (args[i].StartsWith('@') && args[i].Length > 1 && responseFile is null)
                        responseFile = Path.GetFullPath(args[i][1..]);
                    else
                        return Usage();
                    break;
            }
        }
        if (outputDirectory is null || responseFile is null) return Usage();

        var resolvedInputs = File.ReadLines(responseFile)
            .Select(x => x.Trim())
            .Where(x => x.Length != 0)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var inputs = resolvedInputs
            // Response-file mode is the MSBuild/reference-set contract. The authoritative stdlib declaration surface
            // is already supplied as the frontend KLIB, so marked CLR stdlib twins produce no projected KLIB.
            // They remain in `resolvedInputs`: referenced delegate TypeRefs are decoded from their actual TypeDefs,
            // exactly like delegates in any other reference assembly.
            .Where(input => !IsStandardLibrary(input))
            .ToArray();
        var work = inputs.Select(input => (
            Input: input,
            Output: Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(input) + ".klib")))
            .ToArray();
        // Kotlin classifiers cannot overload on generic arity. Compute the
        // stable source-name rule once from the complete MSBuild-resolved
        // reference set, then give every otherwise-independent worker the
        // same tiny naming catalog (Task + Task`1 -> Task / Task1; a singleton
        // List`1 remains List).
        var arityClashes = DiscoverArityClashes(inputs);
        var delegateCatalog = DelegateReferenceCatalog.Discover(resolvedInputs);
        var delegateCatalogJson = delegateCatalog.Serialize();
        var companionCatalog = CompanionReferenceCatalog.Discover(resolvedInputs);
        var companionCatalogJson = companionCatalog.Serialize();
        var innerCatalog = InnerReferenceCatalog.Discover(resolvedInputs);
        var innerCatalogJson = innerCatalog.Serialize();
        var collisions = work.GroupBy(x => x.Output, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Select(y => y.Input).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .ToArray();
        if (collisions.Length != 0)
            throw new InvalidOperationException(
                "different reference assemblies map to the same KLIB output: " +
                string.Join(", ", collisions.Select(x => Path.GetFileName(x.Key))));

        Directory.CreateDirectory(outputDirectory);
        var projectionCatalogPath = Path.Combine(outputDirectory, ".dll2klib-projection-catalog.json");
        var projectionCatalog = JsonSerializer.Serialize(new {
            Version = 1,
            ArityClashes = arityClashes,
            Delegates = JsonSerializer.Deserialize<JsonElement>(delegateCatalogJson),
            Companions = JsonSerializer.Deserialize<JsonElement>(companionCatalogJson),
            Inners = JsonSerializer.Deserialize<JsonElement>(innerCatalogJson),
        });
        var projectionCatalogChanged =
            !File.Exists(projectionCatalogPath) ||
            !StringComparer.Ordinal.Equals(File.ReadAllText(projectionCatalogPath), projectionCatalog);
        var tool = Path.GetFullPath(typeof(Program).Assembly.Location);
        var toolTime = File.GetLastWriteTimeUtc(tool);
        var stale = work.Where(x =>
        {
            if (projectionCatalogChanged) return true;
            if (!File.Exists(x.Output)) return true;
            var outputTime = File.GetLastWriteTimeUtc(x.Output);
            return outputTime < File.GetLastWriteTimeUtc(x.Input) ||
                outputTime < toolTime ||
                delegateCatalog.DependenciesOf(x.Input).Any(path =>
                    outputTime < File.GetLastWriteTimeUtc(path)) ||
                companionCatalog.DependenciesOf(x.Input).Any(path =>
                    outputTime < File.GetLastWriteTimeUtc(path)) ||
                innerCatalog.DependenciesOf(x.Input).Any(path =>
                    outputTime < File.GetLastWriteTimeUtc(path));
        }).ToArray();
        if (stale.Length == 0)
        {
            Console.WriteLine($"dll2klib: {work.Length} KLIB(s) up to date");
            return 0;
        }

        var catalogPath = Path.Combine(
            outputDirectory,
            $".dll2klib-delegates-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        var companionCatalogPath = Path.Combine(
            outputDirectory,
            $".dll2klib-companions-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        var innerCatalogPath = Path.Combine(
            outputDirectory,
            $".dll2klib-inners-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        File.WriteAllText(catalogPath, delegateCatalogJson);
        File.WriteAllText(companionCatalogPath, companionCatalogJson);
        File.WriteAllText(innerCatalogPath, innerCatalogJson);
        try
        {
            var parallelism = jobs == 0 ? stale.Length : Math.Max(1, Math.Min(jobs, stale.Length));
            Console.WriteLine($"dll2klib: converting {stale.Length}/{work.Length} reference(s), jobs={parallelism}");
            using var gate = new SemaphoreSlim(parallelism);
            var failures = new List<string>();
            var failureLock = new object();
            await Task.WhenAll(stale.Select(async item =>
            {
                await gate.WaitAsync();
                try
                {
                    var start = new ProcessStartInfo("dotnet") {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    start.ArgumentList.Add(tool);
                    start.ArgumentList.Add(item.Input);
                    start.ArgumentList.Add(item.Output);
                    start.Environment[ArityClashesEnvironment] = string.Join(';', arityClashes);
                    start.Environment[DelegateCatalogEnvironment] = catalogPath;
                    start.Environment[CompanionCatalogEnvironment] = companionCatalogPath;
                    start.Environment[InnerCatalogEnvironment] = innerCatalogPath;
                    using var child = Process.Start(start)
                        ?? throw new InvalidOperationException($"failed to start worker for {item.Input}");
                    var stdout = child.StandardOutput.ReadToEndAsync();
                    var stderr = child.StandardError.ReadToEndAsync();
                    await child.WaitForExitAsync();
                    var output = await stdout;
                    var error = await stderr;
                    if (output.Length != 0) Console.Out.Write(output);
                    if (error.Length != 0) Console.Error.Write(error);
                    if (child.ExitCode != 0)
                        lock (failureLock) failures.Add($"{item.Input} (exit {child.ExitCode})");
                }
                finally
                {
                    gate.Release();
                }
            }));
            if (failures.Count != 0)
                throw new InvalidOperationException("worker conversion failed: " + string.Join(", ", failures));
            WriteAllTextAtomically(projectionCatalogPath, projectionCatalog);
            return 0;
        }
        finally
        {
            if (File.Exists(catalogPath)) File.Delete(catalogPath);
            if (File.Exists(companionCatalogPath)) File.Delete(companionCatalogPath);
            if (File.Exists(innerCatalogPath)) File.Delete(innerCatalogPath);
        }
    }

    private static void WriteAllTextAtomically(string path, string contents)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "usage:\n" +
            "  dll2klib <reference.dll> <output.klib>  (internal batch worker)\n" +
            "  dll2klib --out <directory> [--jobs <N>] @<references.rsp>\n" +
            "  --jobs 0 starts one worker per stale reference");
        return 2;
    }

    private static bool IsStandardLibrary(string input)
    {
        using var file = File.OpenRead(input);
        using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null) return false;
        return new MetadataAttributes(pe.GetMetadataReader()).IsStandardLibrary;
    }

    private static void Convert(string input, string output)
    {
        using var file = File.OpenRead(input);
        using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null)
            throw new InvalidDataException($"not a managed PE: {input}");

        var md = pe.GetMetadataReader();
        var assemblyName = md.IsAssembly ? md.GetString(md.GetAssemblyDefinition().Name) : Path.GetFileNameWithoutExtension(input);
        var moduleName = $"clr.{assemblyName}.{md.GetGuid(md.GetModuleDefinition().Mvid):N}";
        var arityNames = ArityNames.Create(md, Environment.GetEnvironmentVariable(ArityClashesEnvironment));
        var delegateCatalog = DelegateReferenceCatalog.Load(
            Environment.GetEnvironmentVariable(DelegateCatalogEnvironment));
        var companionCatalog = CompanionReferenceCatalog.Load(
            Environment.GetEnvironmentVariable(CompanionCatalogEnvironment));
        var innerCatalog = InnerReferenceCatalog.Load(
            Environment.GetEnvironmentVariable(InnerCatalogEnvironment));
        var fragments = new AssemblyScanner(md, arityNames, delegateCatalog, companionCatalog, innerCatalog).Scan();

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                Write(zip, "default/manifest", Manifest(moduleName));
                var header = new Header { ModuleName = moduleName };
                header.PackageFragmentName.Add(fragments.Select(x => x.PackageName));
                Write(zip, "default/linkdata/module", header.ToByteArray());
                foreach (var fragment in fragments)
                {
                    var dir = string.IsNullOrEmpty(fragment.PackageName)
                        ? "default/linkdata/root_package"
                        : "default/linkdata/package_" + fragment.PackageName;
                    var shortName = fragment.PackageName.Split('.').LastOrDefault() ?? "";
                    Write(zip, $"{dir}/0_{shortName}.knm", fragment.Message.ToByteArray());
                }
            }
            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        Console.WriteLine($"{Path.GetFileName(input)} -> {Path.GetFileName(output)}: {fragments.Sum(x => x.Message.Class.Count)} public class(es)");
    }

    private static byte[] Manifest(string moduleName) => System.Text.Encoding.UTF8.GetBytes(
        "abi_version=2.4.0\n" +
        "compiler_version=2.4.0\n" +
        "ir_signature_versions=1,2\n" +
        "metadata_version=2.4.0\n" +
        $"unique_name={moduleName}\n");

    private static IReadOnlyList<string> DiscoverArityClashes(IEnumerable<string> inputs)
    {
        var members = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            using var file = File.OpenRead(input);
            using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            string ScopeOf(TypeDefinitionHandle handle)
            {
                var def = md.GetTypeDefinition(handle);
                var parent = def.GetDeclaringType();
                var name = md.GetString(def.Name);
                if (!parent.IsNil) return ScopeOf(parent) + "." + name;
                var ns = md.GetString(def.Namespace);
                return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            }
            foreach (var handle in md.TypeDefinitions)
            {
                var def = md.GetTypeDefinition(handle);
                var attrs = def.Attributes & TypeAttributes.VisibilityMask;
                if (def.GetDeclaringType().IsNil)
                {
                    if (attrs != TypeAttributes.Public) continue;
                }
                else if (attrs is not (TypeAttributes.NestedPublic
                    or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem)) continue;
                var metadataName = md.GetString(def.Name);
                var tick = metadataName.IndexOf('`');
                var simple = tick < 0 ? metadataName : metadataName[..tick];
                var arity = tick < 0 || !int.TryParse(metadataName[(tick + 1)..], out var parsed)
                    ? 0 : parsed;
                var scope = def.GetDeclaringType().IsNil
                    ? md.GetString(def.Namespace)
                    : ScopeOf(def.GetDeclaringType());
                var key = string.IsNullOrEmpty(scope) ? simple : scope + "." + simple;
                if (!members.TryGetValue(key, out var arities))
                    members[key] = arities = new HashSet<int>();
                arities.Add(arity);
            }
        }
        return members.Where(x => x.Value.Count > 1)
            .Select(x => x.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var output = entry.Open();
        output.Write(bytes);
    }
}

internal sealed record DelegateCatalogEntry(
    string AssemblyName,
    string MetadataName,
    string DefinitionPath,
    int TypeDefinitionRow);

internal sealed class DelegateReferenceCatalog
{
    private readonly Dictionary<string, DelegateCatalogEntry> _entries;

    private DelegateReferenceCatalog(IEnumerable<DelegateCatalogEntry> entries)
    {
        _entries = new Dictionary<string, DelegateCatalogEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var key = Key(entry.AssemblyName, entry.MetadataName);
            if (_entries.TryGetValue(key, out var existing) &&
                (!StringComparer.Ordinal.Equals(existing.DefinitionPath, entry.DefinitionPath) ||
                 existing.TypeDefinitionRow != entry.TypeDefinitionRow))
                throw new InvalidOperationException(
                    $"delegate '{entry.MetadataName}' is defined more than once for assembly '{entry.AssemblyName}'");
            _entries[key] = entry;
        }
    }

    public static DelegateReferenceCatalog Empty { get; } = new(Array.Empty<DelegateCatalogEntry>());

    public static DelegateReferenceCatalog Discover(IEnumerable<string> inputs)
    {
        var paths = inputs.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
        var definitions = new List<DelegateCatalogEntry>();
        foreach (var path in paths)
        {
            using var file = File.OpenRead(path);
            using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            var assemblyName = AssemblyName(md, path);
            foreach (var handle in md.TypeDefinitions)
            {
                var def = md.GetTypeDefinition(handle);
                if (!IsMulticastDelegate(md, def.BaseType)) continue;
                var metadataName = DefinitionName(md, handle);
                if (IsBuiltinDelegate(metadataName)) continue;
                definitions.Add(new DelegateCatalogEntry(
                    assemblyName,
                    metadataName,
                    path,
                    MetadataTokens.GetRowNumber(handle)));
            }
        }

        var result = new DelegateReferenceCatalog(definitions);
        // Preserve type-forwarder identity. A consuming signature names the
        // forwarding assembly in its TypeRef even though the delegate TypeDef
        // and Invoke signature live in the implementation assembly.
        var aliases = new List<DelegateCatalogEntry>();
        foreach (var path in paths)
        {
            using var file = File.OpenRead(path);
            using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            var forwardingAssembly = AssemblyName(md, path);
            foreach (var handle in md.ExportedTypes)
            {
                var exported = md.GetExportedType(handle);
                const TypeAttributes Forwarder = (TypeAttributes)0x00200000;
                if ((exported.Attributes & Forwarder) == 0) continue;
                var targetAssembly = ExportedAssemblyName(md, handle);
                if (targetAssembly is null) continue;
                var metadataName = ExportedName(md, handle);
                if (!result.TryGet(targetAssembly, metadataName, out var target)) continue;
                aliases.Add(target with {
                    AssemblyName = forwardingAssembly,
                    MetadataName = metadataName,
                });
            }
        }
        return aliases.Count == 0
            ? result
            : new DelegateReferenceCatalog(definitions.Concat(aliases));
    }

    public static DelegateReferenceCatalog Load(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Empty;
        var entries = JsonSerializer.Deserialize<List<DelegateCatalogEntry>>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"invalid delegate catalog: {path}");
        return new DelegateReferenceCatalog(entries);
    }

    public string Serialize() => JsonSerializer.Serialize(
        _entries.Values
            .OrderBy(x => x.AssemblyName, StringComparer.Ordinal)
            .ThenBy(x => x.MetadataName, StringComparer.Ordinal)
            .ToArray());

    public bool TryResolve(
        MetadataReader reader,
        TypeReferenceHandle handle,
        out DelegateCatalogEntry entry)
    {
        var assemblyName = ReferenceAssemblyName(reader, handle);
        if (assemblyName is not null &&
            TryGet(assemblyName, ReferenceName(reader, handle), out entry))
            return true;
        entry = null!;
        return false;
    }

    public IReadOnlyList<string> DependenciesOf(string input)
    {
        using var file = File.OpenRead(input);
        using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata) return Array.Empty<string>();
        var md = pe.GetMetadataReader();
        return md.TypeReferences
            .Select(handle => TryResolve(md, handle, out var entry) ? entry.DefinitionPath : null)
            .Where(path => path is not null && !StringComparer.Ordinal.Equals(path, input))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private bool TryGet(string assemblyName, string metadataName, out DelegateCatalogEntry entry) =>
        _entries.TryGetValue(Key(assemblyName, metadataName), out entry!);

    private static string Key(string assemblyName, string metadataName) =>
        assemblyName + "\0" + metadataName;

    private static string AssemblyName(MetadataReader md, string path) =>
        md.IsAssembly
            ? md.GetString(md.GetAssemblyDefinition().Name)
            : Path.GetFileNameWithoutExtension(path);

    private static bool IsMulticastDelegate(MetadataReader md, EntityHandle handle)
    {
        if (handle.Kind != HandleKind.TypeReference) return false;
        var type = md.GetTypeReference((TypeReferenceHandle)handle);
        return md.GetString(type.Namespace) == "System" &&
            md.GetString(type.Name) == "MulticastDelegate";
    }

    private static bool IsBuiltinDelegate(string metadataName)
    {
        var simple = metadataName[(metadataName.LastIndexOfAny(['.', '+']) + 1)..];
        return metadataName.StartsWith("System.", StringComparison.Ordinal) &&
            (simple == "Action" ||
             simple.StartsWith("Action`", StringComparison.Ordinal) ||
             simple.StartsWith("Func`", StringComparison.Ordinal) ||
             simple == "EventHandler" ||
             simple.StartsWith("EventHandler`", StringComparison.Ordinal));
    }

    private static string DefinitionName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var def = md.GetTypeDefinition(handle);
        var simple = md.GetString(def.Name);
        var parent = def.GetDeclaringType();
        if (!parent.IsNil) return DefinitionName(md, parent) + "+" + simple;
        var ns = md.GetString(def.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    private static string ReferenceName(MetadataReader md, TypeReferenceHandle handle)
    {
        var type = md.GetTypeReference(handle);
        var simple = md.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            return ReferenceName(md, (TypeReferenceHandle)type.ResolutionScope) + "+" + simple;
        var ns = md.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    private static string? ReferenceAssemblyName(MetadataReader md, TypeReferenceHandle handle)
    {
        var scope = md.GetTypeReference(handle).ResolutionScope;
        return scope.Kind switch {
            HandleKind.AssemblyReference => md.GetString(
                md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name),
            HandleKind.TypeReference => ReferenceAssemblyName(md, (TypeReferenceHandle)scope),
            _ => null,
        };
    }

    private static string ExportedName(MetadataReader md, ExportedTypeHandle handle)
    {
        var type = md.GetExportedType(handle);
        var simple = md.GetString(type.Name);
        if (type.Implementation.Kind == HandleKind.ExportedType)
            return ExportedName(md, (ExportedTypeHandle)type.Implementation) + "+" + simple;
        var ns = md.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    private static string? ExportedAssemblyName(MetadataReader md, ExportedTypeHandle handle)
    {
        var implementation = md.GetExportedType(handle).Implementation;
        return implementation.Kind switch {
            HandleKind.AssemblyReference => md.GetString(
                md.GetAssemblyReference((AssemblyReferenceHandle)implementation).Name),
            HandleKind.ExportedType => ExportedAssemblyName(md, (ExportedTypeHandle)implementation),
            _ => null,
        };
    }
}

internal sealed record InnerCatalogEntry(
    string AssemblyIdentity,
    string MetadataName,
    int CapturedCount,
    int[] SemanticArgumentOrder,
    string DefinitionPath);

// A TypeRef does not carry custom attributes. Resolve KotlinInner only through the exact TypeDef in the complete
// batch reference universe, keyed by assembly + arity-bearing metadata path. This lets assembly B re-export
// A.Outer<T>.Inner<U> in a signature without leaking the CLR capture prefix into B's projected KLIB.
internal sealed class InnerReferenceCatalog
{
    private readonly Dictionary<string, InnerCatalogEntry> _entries;

    private InnerReferenceCatalog(IEnumerable<InnerCatalogEntry> entries)
    {
        _entries = new Dictionary<string, InnerCatalogEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var key = Key(entry.AssemblyIdentity, entry.MetadataName);
            if (_entries.TryGetValue(key, out var existing) &&
                (existing.CapturedCount != entry.CapturedCount ||
                 !existing.SemanticArgumentOrder.SequenceEqual(entry.SemanticArgumentOrder) ||
                 !StringComparer.Ordinal.Equals(existing.DefinitionPath, entry.DefinitionPath)))
                throw new InvalidOperationException(
                    $"Kotlin inner type '{entry.MetadataName}' is ambiguous for assembly '{entry.AssemblyIdentity}'");
            _entries[key] = entry;
        }
    }

    public static InnerReferenceCatalog Empty { get; } = new(Array.Empty<InnerCatalogEntry>());

    public static InnerReferenceCatalog Discover(IEnumerable<string> inputs)
    {
        var inputPaths = inputs.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
        var entries = new List<InnerCatalogEntry>();
        foreach (var path in inputPaths)
        {
            using var file = File.OpenRead(path);
            using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            var attrs = new MetadataAttributes(md);
            if (!attrs.IsDotKtAssembly) continue;
            var assemblyIdentity = AssemblyIdentity(md);
            foreach (var handle in md.TypeDefinitions)
            {
                if (attrs.Int32(handle, MetadataAttributes.DotKtNs + "KotlinInnerAttribute") is not int captured)
                    continue;
                var parameterCount = md.GetTypeDefinition(handle).GetGenericParameters().Count;
                if (captured < 0 || captured > parameterCount)
                    throw new InvalidDataException(
                        $"Kotlin inner type '{DefinitionName(md, handle)}' carries invalid captured count {captured}");
                entries.Add(new InnerCatalogEntry(
                    assemblyIdentity, DefinitionName(md, handle), captured,
                    SemanticArgumentOrder(md, handle), path));
            }
        }
        var forwarders = new List<(string ForwardingIdentity, string TargetIdentity, string MetadataName)>();
        foreach (var path in inputPaths)
        {
            using var file = File.OpenRead(path);
            using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            var forwardingIdentity = AssemblyIdentity(md);
            foreach (var handle in md.ExportedTypes)
            {
                var exported = md.GetExportedType(handle);
                const TypeAttributes Forwarder = (TypeAttributes)0x00200000;
                if ((exported.Attributes & Forwarder) == 0) continue;
                var targetIdentity = ExportedAssemblyIdentity(md, handle);
                if (targetIdentity is null) continue;
                var metadataName = ExportedName(md, handle);
                forwarders.Add((forwardingIdentity, targetIdentity, metadataName));
            }
        }

        // ECMA forwarding normally exports only the top-level Outer. A nested TypeRef is scoped through that
        // forwarded Outer TypeRef, so alias every trusted inner below the forwarded path. Resolve to a fixed point:
        // Facade2 -> Facade1 -> Definition must work regardless of input ordering, and Facade1's alias is itself the
        // source from which Facade2 is derived.
        var all = new InnerReferenceCatalog(entries);
        var changed = true;
        while (changed)
        {
            changed = false;
            var additions = new List<InnerCatalogEntry>();
            foreach (var forwarder in forwarders)
                foreach (var target in all._entries.Values.Where(entry =>
                    StringComparer.Ordinal.Equals(entry.AssemblyIdentity, forwarder.TargetIdentity) &&
                    (StringComparer.Ordinal.Equals(entry.MetadataName, forwarder.MetadataName) ||
                     entry.MetadataName.StartsWith(forwarder.MetadataName + "+", StringComparison.Ordinal))))
                {
                    var alias = target with { AssemblyIdentity = forwarder.ForwardingIdentity };
                    if (!all._entries.ContainsKey(Key(alias.AssemblyIdentity, alias.MetadataName)))
                    {
                        additions.Add(alias);
                        changed = true;
                    }
                }
            if (changed) all = new InnerReferenceCatalog(all._entries.Values.Concat(additions));
        }
        return all;
    }

    public static InnerReferenceCatalog Load(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Empty;
        var entries = JsonSerializer.Deserialize<List<InnerCatalogEntry>>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"invalid inner catalog: {path}");
        return new InnerReferenceCatalog(entries);
    }

    public string Serialize() => JsonSerializer.Serialize(
        _entries.Values.OrderBy(x => x.AssemblyIdentity, StringComparer.Ordinal)
            .ThenBy(x => x.MetadataName, StringComparer.Ordinal).ToArray());

    public bool TryResolve(MetadataReader reader, TypeReferenceHandle handle, out InnerCatalogEntry entry)
    {
        var assemblyIdentity = ReferenceAssemblyIdentity(reader, handle);
        if (assemblyIdentity is not null && TryGet(assemblyIdentity, ReferenceName(reader, handle), out entry))
            return true;
        entry = null!;
        return false;
    }

    public IReadOnlyList<string> DependenciesOf(string input)
    {
        using var file = File.OpenRead(input);
        using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata) return Array.Empty<string>();
        var md = pe.GetMetadataReader();
        return md.TypeReferences
            .Select(handle => TryResolve(md, handle, out var entry) ? entry.DefinitionPath : null)
            .Where(path => path is not null && !StringComparer.Ordinal.Equals(path, input))
            .Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
    }

    private bool TryGet(string assemblyIdentity, string metadataName, out InnerCatalogEntry entry) =>
        _entries.TryGetValue(Key(assemblyIdentity, metadataName), out entry!);

    private static string Key(string assemblyIdentity, string metadataName) => assemblyIdentity + "\0" + metadataName;

    private static string DefinitionName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var def = md.GetTypeDefinition(handle);
        var simple = md.GetString(def.Name);
        var parent = def.GetDeclaringType();
        if (!parent.IsNil) return DefinitionName(md, parent) + "+" + simple;
        var ns = md.GetString(def.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    internal static int[] SemanticArgumentOrder(MetadataReader md, TypeDefinitionHandle handle)
    {
        var definition = md.GetTypeDefinition(handle);
        var total = definition.GetGenericParameters().Count;
        var parent = definition.GetDeclaringType();
        if (parent.IsNil) return Enumerable.Range(0, total).ToArray();
        var parentTotal = md.GetTypeDefinition(parent).GetGenericParameters().Count;
        if (parentTotal > total)
            throw new InvalidDataException(
                $"nested type '{DefinitionName(md, handle)}' declares fewer generic slots than its owner");
        return Enumerable.Range(parentTotal, total - parentTotal)
            .Concat(SemanticArgumentOrder(md, parent))
            .ToArray();
    }

    private static string ReferenceName(MetadataReader md, TypeReferenceHandle handle)
    {
        var type = md.GetTypeReference(handle);
        var simple = md.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            return ReferenceName(md, (TypeReferenceHandle)type.ResolutionScope) + "+" + simple;
        var ns = md.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    private static string AssemblyIdentity(MetadataReader md)
    {
        if (!md.IsAssembly) throw new InvalidDataException("inner catalog requires an assembly definition");
        var assembly = md.GetAssemblyDefinition();
        return AssemblyIdentity(md.GetString(assembly.Name), assembly.Version, md.GetString(assembly.Culture),
            md.GetBlobBytes(assembly.PublicKey), publicKey: true);
    }

    private static string? ReferenceAssemblyIdentity(MetadataReader md, TypeReferenceHandle handle)
    {
        var scope = md.GetTypeReference(handle).ResolutionScope;
        return scope.Kind switch {
            HandleKind.AssemblyReference => AssemblyIdentity(md, (AssemblyReferenceHandle)scope),
            HandleKind.TypeReference => ReferenceAssemblyIdentity(md, (TypeReferenceHandle)scope),
            _ => null,
        };
    }

    private static string AssemblyIdentity(MetadataReader md, AssemblyReferenceHandle handle)
    {
        var assembly = md.GetAssemblyReference(handle);
        return AssemblyIdentity(md.GetString(assembly.Name), assembly.Version, md.GetString(assembly.Culture),
            md.GetBlobBytes(assembly.PublicKeyOrToken),
            publicKey: (assembly.Flags & AssemblyFlags.PublicKey) != 0);
    }

    private static string AssemblyIdentity(string name, Version version, string culture, byte[] key, bool publicKey)
    {
        var assembly = new AssemblyName(name) {
            Version = version,
            CultureName = string.IsNullOrEmpty(culture) ? null : culture,
        };
        if (key.Length != 0)
        {
            if (publicKey) assembly.SetPublicKey(key);
            else assembly.SetPublicKeyToken(key);
        }
        return assembly.FullName
            ?? throw new InvalidDataException($"could not form assembly identity for '{name}'");
    }

    private static string ExportedName(MetadataReader md, ExportedTypeHandle handle)
    {
        var type = md.GetExportedType(handle);
        var simple = md.GetString(type.Name);
        if (type.Implementation.Kind == HandleKind.ExportedType)
            return ExportedName(md, (ExportedTypeHandle)type.Implementation) + "+" + simple;
        var ns = md.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    private static string? ExportedAssemblyIdentity(MetadataReader md, ExportedTypeHandle handle)
    {
        var implementation = md.GetExportedType(handle).Implementation;
        return implementation.Kind switch {
            HandleKind.AssemblyReference => AssemblyIdentity(md, (AssemblyReferenceHandle)implementation),
            HandleKind.ExportedType => ExportedAssemblyIdentity(md, (ExportedTypeHandle)implementation),
            _ => null,
        };
    }
}

internal sealed record CompanionCatalogEntry(
    string AssemblyIdentity,
    string MetadataName,
    string SemanticPackage,
    string[] SemanticClasses,
    string DefinitionPath);

internal static class CompanionMetadataSyntax
{
    private static readonly char[] ForbiddenSegmentCharacters = ['.', '/', '\\', '<', '>', ':', '[', ']', '$'];

    public static bool IsSegment(string value) =>
        value.Length != 0 && value.IndexOfAny(ForbiddenSegmentCharacters) < 0 &&
        !value.Any(char.IsControl);

    public static bool IsQualifiedName(string value) =>
        value.Split('.', StringSplitOptions.None).All(IsSegment);

    public static bool IsCarrierKind(string value) => value is "nested" or "sidecar";
}

// The one physical shape check for a trusted [KotlinCompanion] carrier, shared by the reference catalog and the
// projecting scanner so both accept exactly the same metadata. A carrier is NESTED in its physical owner when that
// owner is non-generic, and HOISTED to a top-level sidecar when it is generic: CLR static storage belongs to each
// closed constructed type, so a nested carrier of a generic owner would hold one singleton per instantiation instead
// of the single one its Kotlin declaration means. Neither shape declares generic parameters of its own.
internal static class CompanionCarrierShape
{
    public static FieldDefinitionHandle Validate(
        MetadataReader md,
        MetadataAttributes attrs,
        TypeDefinitionHandle carrierHandle,
        TypeDefinitionHandle ownerHandle,
        string kind,
        int physicalOwnerArity,
        string carrierName,
        string ownerName,
        string claimedPhysicalOwner)
    {
        var carrier = md.GetTypeDefinition(carrierHandle);
        if (kind == "sidecar")
        {
            if (physicalOwnerArity == 0)
                throw new InvalidDataException(
                    $"hoisted Kotlin companion carrier '{carrierName}' requires a generic physical owner");
            if (!carrier.GetDeclaringType().IsNil)
                throw new InvalidDataException(
                    $"hoisted Kotlin companion carrier '{carrierName}' must be a top-level type");
            if ((carrier.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public)
                throw new InvalidDataException(
                    $"hoisted Kotlin companion carrier '{carrierName}' must be public");
        }
        else
        {
            if (physicalOwnerArity != 0)
                throw new InvalidDataException(
                    $"nested Kotlin companion carrier '{carrierName}' requires a non-generic physical owner");
            if (carrier.GetDeclaringType() != ownerHandle)
                throw new InvalidDataException(
                    $"nested Kotlin companion carrier '{carrierName}' must be an ordinary nested type of its " +
                    $"physical owner '{ownerName}' (metadata claimed '{claimedPhysicalOwner}')");
            if ((carrier.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.NestedPublic)
                throw new InvalidDataException(
                    $"nested Kotlin companion carrier '{carrierName}' must have NestedPublic visibility");
        }
        if (carrier.GetGenericParameters().Count != 0)
            throw new InvalidDataException(
                $"Kotlin companion carrier '{carrierName}' must declare no generic parameters");
        if (!attrs.Has(carrierHandle, MetadataAttributes.DotKtNs + "KotlinObjectAttribute"))
            throw new InvalidDataException($"Kotlin companion carrier '{carrierName}' requires [KotlinObject]");
        var instances = carrier.GetFields()
            .Where(fieldHandle => IsExactSingletonInstanceField(md, carrierHandle, fieldHandle))
            .ToArray();
        if (instances.Length != 1)
            throw new InvalidDataException(
                $"Kotlin companion carrier '{carrierName}' requires one public static self-typed $INSTANCE field");
        return instances[0];
    }

    private static bool IsExactSingletonInstanceField(
        MetadataReader md,
        TypeDefinitionHandle carrierHandle,
        FieldDefinitionHandle fieldHandle)
    {
        var field = md.GetFieldDefinition(fieldHandle);
        if (md.GetString(field.Name) != "$INSTANCE" ||
            (field.Attributes & FieldAttributes.Static) == 0 ||
            (field.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public)
            return false;
        var context = new GenericContext(
            carrierHandle,
            default,
            ImmutableDictionary<GenericParameterHandle, int>.Empty);
        var actual = field.DecodeSignature(RawSignatureTypeProvider.Instance, context);
        var expected = RawSignatureTypeProvider.Instance.GetTypeFromDefinition(
            md, carrierHandle, rawTypeKind: 0x12); // ELEMENT_TYPE_CLASS
        return StringComparer.Ordinal.Equals(actual, expected);
    }
}

// A TypeRef carries only the CLR carrier identity. Recovering its Kotlin companion identity therefore requires the
// compiler-owned carrier on the referenced TypeDef. Build that trusted relation once from the complete resolved
// reference set and give every worker the same exact (assembly identity, metadata name) catalog. No CLR suffix or
// source-name convention participates in the lookup.
internal sealed class CompanionReferenceCatalog
{
    private sealed record Carrier(
        string Kind,
        string Owner,
        string Name,
        string Visibility,
        string PhysicalOwner,
        int PhysicalOwnerArity);

    private readonly Dictionary<string, CompanionCatalogEntry> _entries;

    private CompanionReferenceCatalog(IEnumerable<CompanionCatalogEntry> entries)
    {
        _entries = new Dictionary<string, CompanionCatalogEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var key = Key(entry.AssemblyIdentity, entry.MetadataName);
            if (_entries.TryGetValue(key, out var existing) &&
                (!StringComparer.Ordinal.Equals(existing.SemanticPackage, entry.SemanticPackage) ||
                 !existing.SemanticClasses.SequenceEqual(entry.SemanticClasses, StringComparer.Ordinal) ||
                 !StringComparer.Ordinal.Equals(existing.DefinitionPath, entry.DefinitionPath)))
                throw new InvalidOperationException(
                    $"companion carrier '{entry.MetadataName}' is ambiguous for assembly '{entry.AssemblyIdentity}'");
            _entries[key] = entry;
        }
    }

    public static CompanionReferenceCatalog Empty { get; } = new(Array.Empty<CompanionCatalogEntry>());

    public static CompanionReferenceCatalog Discover(IEnumerable<string> inputs)
    {
        var paths = inputs.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
        var definitions = new List<CompanionCatalogEntry>();
        foreach (var path in paths)
        {
            using var file = File.OpenRead(path);
            using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            var attrs = new MetadataAttributes(md);
            var physicalTypes = md.TypeDefinitions
                .Select(handle => (
                    Handle: handle,
                    Name: PhysicalName(md, handle),
                    Arity: md.GetTypeDefinition(handle).GetGenericParameters().Count))
                .GroupBy(x => (x.Name, x.Arity))
                .ToDictionary(g => g.Key, g => g.Select(x => x.Handle).ToArray());
            var claimedOwners = new HashSet<TypeDefinitionHandle>();
            var semanticOwners = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
            foreach (var handle in md.TypeDefinitions)
            {
                if (ReadCarrier(attrs, handle) is not { } carrier) continue;
                if (!physicalTypes.TryGetValue((carrier.PhysicalOwner, carrier.PhysicalOwnerArity), out var owners) ||
                    owners.Length != 1)
                    throw new InvalidDataException(
                        $"Kotlin companion owner '{carrier.PhysicalOwner}' arity {carrier.PhysicalOwnerArity} " +
                        $"resolved to {(owners is null ? 0 : owners.Length)} physical types");
                var owner = owners[0];
                CompanionCarrierShape.Validate(
                    md, attrs, handle, owner, carrier.Kind, carrier.PhysicalOwnerArity,
                    DefinitionName(md, handle), DefinitionName(md, owner), carrier.PhysicalOwner);
                if (!claimedOwners.Add(owner))
                    throw new InvalidDataException($"multiple Kotlin companion carriers name owner '{carrier.Owner}'");
                if (!semanticOwners.TryAdd(carrier.Owner, owner) && semanticOwners[carrier.Owner] != owner)
                    throw new InvalidDataException(
                        $"multiple physical types claim Kotlin companion owner '{carrier.Owner}'");
                if (carrier.Visibility is not ("public" or "protected") ||
                    !IsVisibleType(md, owner) || !IsVisibleType(md, handle))
                    continue;
                var semanticPackage = TopLevelNamespace(md, owner);
                var semanticClassPart = string.IsNullOrEmpty(semanticPackage)
                    ? carrier.Owner
                    : carrier.Owner.StartsWith(semanticPackage + ".", StringComparison.Ordinal)
                        ? carrier.Owner[(semanticPackage.Length + 1)..]
                        : throw new InvalidDataException(
                            $"semantic companion owner '{carrier.Owner}' is outside physical package '{semanticPackage}'");
                definitions.Add(new CompanionCatalogEntry(
                    AssemblyIdentity(md),
                    DefinitionName(md, handle),
                    semanticPackage,
                    semanticClassPart.Split('.', StringSplitOptions.RemoveEmptyEntries).Append(carrier.Name).ToArray(),
                    path));
            }
        }

        var result = new CompanionReferenceCatalog(definitions);
        var aliases = new List<CompanionCatalogEntry>();
        foreach (var path in paths)
        {
            using var file = File.OpenRead(path);
            using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            var forwardingIdentity = AssemblyIdentity(md);
            foreach (var handle in md.ExportedTypes)
            {
                var exported = md.GetExportedType(handle);
                const TypeAttributes Forwarder = (TypeAttributes)0x00200000;
                if ((exported.Attributes & Forwarder) == 0) continue;
                var targetIdentity = ExportedAssemblyIdentity(md, handle);
                if (targetIdentity is null) continue;
                var metadataName = ExportedName(md, handle);
                if (!result.TryGet(targetIdentity, metadataName, out var target)) continue;
                aliases.Add(target with {
                    AssemblyIdentity = forwardingIdentity,
                    MetadataName = metadataName,
                });
            }
        }
        return aliases.Count == 0
            ? result
            : new CompanionReferenceCatalog(definitions.Concat(aliases));
    }

    public static CompanionReferenceCatalog Load(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Empty;
        var entries = JsonSerializer.Deserialize<List<CompanionCatalogEntry>>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"invalid companion catalog: {path}");
        return new CompanionReferenceCatalog(entries);
    }

    public string Serialize() => JsonSerializer.Serialize(
        _entries.Values
            .OrderBy(x => x.AssemblyIdentity, StringComparer.Ordinal)
            .ThenBy(x => x.MetadataName, StringComparer.Ordinal)
            .ToArray());

    public bool TryResolve(
        MetadataReader reader,
        TypeReferenceHandle handle,
        out CompanionCatalogEntry entry)
    {
        var assemblyIdentity = ReferenceAssemblyIdentity(reader, handle);
        if (assemblyIdentity is not null &&
            TryGet(assemblyIdentity, ReferenceName(reader, handle), out entry))
            return true;
        entry = null!;
        return false;
    }

    public IReadOnlyList<string> DependenciesOf(string input)
    {
        using var file = File.OpenRead(input);
        using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata) return Array.Empty<string>();
        var md = pe.GetMetadataReader();
        return md.TypeReferences
            .Select(handle => TryResolve(md, handle, out var entry) ? entry.DefinitionPath : null)
            .Where(path => path is not null && !StringComparer.Ordinal.Equals(path, input))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private bool TryGet(string assemblyIdentity, string metadataName, out CompanionCatalogEntry entry) =>
        _entries.TryGetValue(Key(assemblyIdentity, metadataName), out entry!);

    private static string Key(string assemblyIdentity, string metadataName) =>
        assemblyIdentity + "\0" + metadataName;

    private static Carrier? ReadCarrier(MetadataAttributes attrs, TypeDefinitionHandle handle)
    {
        using var doc = attrs.CarrierDocument(handle, MetadataAttributes.DotKtNs + "KotlinCompanionAttribute");
        if (doc is null) return null;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("kind", out var kindNode) || kindNode.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("name", out var nameNode) || nameNode.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("malformed [KotlinCompanion] carrier: expected kind and name strings");
        var kind = kindNode.GetString()!;
        var name = nameNode.GetString()!;
        if (!CompanionMetadataSyntax.IsCarrierKind(kind) || !CompanionMetadataSyntax.IsSegment(name))
            throw new InvalidDataException(
                "malformed [KotlinCompanion] carrier: invalid kind or semantic name segment");
        if (!root.TryGetProperty("owner", out var ownerNode) || ownerNode.ValueKind != JsonValueKind.String ||
            ownerNode.GetString() is not string owner || !CompanionMetadataSyntax.IsQualifiedName(owner))
            throw new InvalidDataException(
                "malformed [KotlinCompanion] carrier: owner must be a non-empty qualified semantic name");
        if (!root.TryGetProperty("physicalOwner", out var physicalOwnerNode) ||
            physicalOwnerNode.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(physicalOwnerNode.GetString()) ||
            !root.TryGetProperty("physicalOwnerArity", out var arityNode) ||
            arityNode.ValueKind != JsonValueKind.Number || !arityNode.TryGetInt32(out var arity) || arity < 0)
            throw new InvalidDataException(
                "malformed [KotlinCompanion] carrier: physicalOwner and physicalOwnerArity are required");
        if (!root.TryGetProperty("visibility", out var visibilityNode) ||
            visibilityNode.ValueKind != JsonValueKind.String ||
            visibilityNode.GetString() is not string visibility ||
            visibility is not ("public" or "internal" or "private" or "protected" or "protectedInternal"))
            throw new InvalidDataException("malformed [KotlinCompanion] carrier: invalid visibility");
        return new Carrier(
            kind, owner, name, visibility, physicalOwnerNode.GetString()!, arity);
    }

    private static bool IsVisibleType(MetadataReader md, TypeDefinitionHandle handle)
    {
        var def = md.GetTypeDefinition(handle);
        var visibility = def.Attributes & TypeAttributes.VisibilityMask;
        var parent = def.GetDeclaringType();
        if (parent.IsNil) return visibility == TypeAttributes.Public;
        return visibility is TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem &&
            IsVisibleType(md, parent);
    }

    private static string DefinitionName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var def = md.GetTypeDefinition(handle);
        var simple = md.GetString(def.Name);
        var parent = def.GetDeclaringType();
        if (!parent.IsNil) return DefinitionName(md, parent) + "+" + simple;
        var ns = md.GetString(def.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    private static string PhysicalName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var def = md.GetTypeDefinition(handle);
        var simple = StripArity(md.GetString(def.Name));
        var parent = def.GetDeclaringType();
        if (!parent.IsNil) return PhysicalName(md, parent) + "+" + simple;
        var ns = md.GetString(def.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    private static string TopLevelNamespace(MetadataReader md, TypeDefinitionHandle handle)
    {
        var current = handle;
        while (true)
        {
            var def = md.GetTypeDefinition(current);
            var parent = def.GetDeclaringType();
            if (parent.IsNil) return md.GetString(def.Namespace);
            current = parent;
        }
    }

    private static string StripArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }

    private static string ReferenceName(MetadataReader md, TypeReferenceHandle handle)
    {
        var type = md.GetTypeReference(handle);
        var simple = md.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            return ReferenceName(md, (TypeReferenceHandle)type.ResolutionScope) + "+" + simple;
        var ns = md.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    private static string AssemblyIdentity(MetadataReader md)
    {
        if (!md.IsAssembly) throw new InvalidDataException("companion carrier catalog requires an assembly definition");
        var assembly = md.GetAssemblyDefinition();
        return AssemblyIdentity(
            md.GetString(assembly.Name),
            assembly.Version,
            md.GetString(assembly.Culture),
            md.GetBlobBytes(assembly.PublicKey),
            publicKey: true);
    }

    private static string? ReferenceAssemblyIdentity(MetadataReader md, TypeReferenceHandle handle)
    {
        var scope = md.GetTypeReference(handle).ResolutionScope;
        if (scope.Kind == HandleKind.TypeReference)
            return ReferenceAssemblyIdentity(md, (TypeReferenceHandle)scope);
        if (scope.Kind != HandleKind.AssemblyReference) return null;
        var assembly = md.GetAssemblyReference((AssemblyReferenceHandle)scope);
        return AssemblyIdentity(
            md.GetString(assembly.Name),
            assembly.Version,
            md.GetString(assembly.Culture),
            md.GetBlobBytes(assembly.PublicKeyOrToken),
            publicKey: (assembly.Flags & AssemblyFlags.PublicKey) != 0);
    }

    private static string AssemblyIdentity(
        string name,
        Version version,
        string culture,
        byte[] key,
        bool publicKey)
    {
        var assembly = new AssemblyName(name) {
            Version = version,
            CultureName = string.IsNullOrEmpty(culture) ? null : culture,
        };
        if (key.Length != 0)
        {
            if (publicKey) assembly.SetPublicKey(key);
            else assembly.SetPublicKeyToken(key);
        }
        return assembly.FullName
            ?? throw new InvalidDataException($"could not form assembly identity for '{name}'");
    }

    private static string ExportedName(MetadataReader md, ExportedTypeHandle handle)
    {
        var type = md.GetExportedType(handle);
        var simple = md.GetString(type.Name);
        if (type.Implementation.Kind == HandleKind.ExportedType)
            return ExportedName(md, (ExportedTypeHandle)type.Implementation) + "+" + simple;
        var ns = md.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    private static string? ExportedAssemblyIdentity(MetadataReader md, ExportedTypeHandle handle)
    {
        var implementation = md.GetExportedType(handle).Implementation;
        if (implementation.Kind == HandleKind.ExportedType)
            return ExportedAssemblyIdentity(md, (ExportedTypeHandle)implementation);
        if (implementation.Kind != HandleKind.AssemblyReference) return null;
        var assembly = md.GetAssemblyReference((AssemblyReferenceHandle)implementation);
        return AssemblyIdentity(
            md.GetString(assembly.Name),
            assembly.Version,
            md.GetString(assembly.Culture),
            md.GetBlobBytes(assembly.PublicKeyOrToken),
            publicKey: (assembly.Flags & AssemblyFlags.PublicKey) != 0);
    }
}

internal sealed record Fragment(string PackageName, PackageFragment Message);

internal sealed class ArityNames
{
    private readonly HashSet<string> _clashes;

    private ArityNames(HashSet<string> clashes) => _clashes = clashes;

    public static ArityNames Create(MetadataReader md, string? inherited)
    {
        var clashes = (inherited ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        string ScopeOf(TypeDefinitionHandle handle)
        {
            var definition = md.GetTypeDefinition(handle);
            var parent = definition.GetDeclaringType();
            var name = md.GetString(definition.Name);
            if (!parent.IsNil) return ScopeOf(parent) + "." + name;
            var ns = md.GetString(definition.Namespace);
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }
        var local = md.TypeDefinitions
            .Select(h => (Handle: h, Definition: md.GetTypeDefinition(h)))
            .Where(x =>
            {
                var visibility = x.Definition.Attributes & TypeAttributes.VisibilityMask;
                return x.Definition.GetDeclaringType().IsNil
                    ? visibility == TypeAttributes.Public
                    : visibility is TypeAttributes.NestedPublic
                        or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem;
            })
            .Select(x => (
                Scope: x.Definition.GetDeclaringType().IsNil
                    ? md.GetString(x.Definition.Namespace)
                    : ScopeOf(x.Definition.GetDeclaringType()),
                Name: md.GetString(x.Definition.Name)))
            .GroupBy(x => FullName(x.Scope, Strip(x.Name)), StringComparer.Ordinal);
        foreach (var family in local)
            if (family.Select(x => Arity(x.Name)).Distinct().Skip(1).Any())
                clashes.Add(family.Key);
        return new ArityNames(clashes);
    }

    public string Simple(string ns, string metadataName)
    {
        var simple = Strip(metadataName);
        var arity = Arity(metadataName);
        return arity > 0 && _clashes.Contains(FullName(ns, simple))
            ? simple + arity.ToString(CultureInfo.InvariantCulture)
            : simple;
    }

    public string Full(string ns, string metadataName) => FullName(ns, Simple(ns, metadataName));

    private static int Arity(string name)
    {
        var tick = name.IndexOf('`');
        return tick >= 0 && int.TryParse(name[(tick + 1)..], out var value) ? value : 0;
    }

    private static string Strip(string name) =>
        name.Contains('`') ? name[..name.IndexOf('`')] : name;

    private static string FullName(string ns, string name) =>
        string.IsNullOrEmpty(ns) ? name : ns + "." + name;
}

internal sealed class AssemblyScanner
{
    private sealed record CompanionCarrier(
        string Kind,
        string Owner,
        string Name,
        string Visibility,
        string PhysicalOwner,
        int PhysicalOwnerArity);

    private sealed record ProjectedFunction(
        MethodDefinitionHandle Handle,
        Function Declaration,
        ImmutableArray<string> PhysicalParameters);

    private sealed record ProjectedConstructor(
        MethodDefinitionHandle Handle,
        Constructor Declaration,
        ImmutableArray<string> PhysicalParameters);

    private readonly MetadataReader _md;
    private readonly MetadataAttributes _attrs;
    private readonly ArityNames _arityNames;
    private readonly DelegateReferenceCatalog _delegateCatalog;
    private readonly CompanionReferenceCatalog _companionCatalog;
    private readonly InnerReferenceCatalog _innerCatalog;
    private readonly Dictionary<TypeDefinitionHandle, CompanionCarrier> _companionCarriers = new();
    private readonly HashSet<TypeDefinitionHandle> _physicalCompanionCarriers = new();
    private readonly Dictionary<TypeDefinitionHandle, TypeDefinitionHandle> _liftedCompanions = new();
    private readonly Dictionary<TypeDefinitionHandle, CompanionCarrier> _companionsByOwner = new();
    private readonly HashSet<FieldDefinitionHandle> _singletonInstanceFields = new();
    private readonly Dictionary<TypeDefinitionHandle, string> _semanticOwnerNames = new();
    private readonly HashSet<TypeDefinitionHandle> _validatedCompanionOwners = new();

    public AssemblyScanner(
        MetadataReader md,
        ArityNames arityNames,
        DelegateReferenceCatalog delegateCatalog,
        CompanionReferenceCatalog companionCatalog,
        InnerReferenceCatalog innerCatalog)
    {
        _md = md;
        _attrs = new MetadataAttributes(md);
        _arityNames = arityNames;
        _delegateCatalog = delegateCatalog;
        _companionCatalog = companionCatalog;
        _innerCatalog = innerCatalog;
        var physicalTypes = md.TypeDefinitions
            .Select(handle => (
                Handle: handle,
                Name: MetadataTypeName(handle),
                Arity: md.GetTypeDefinition(handle).GetGenericParameters().Count))
            .GroupBy(x => (x.Name, x.Arity))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Handle).ToArray());
        var semanticOwners = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
        foreach (var handle in md.TypeDefinitions)
        {
            if (ReadCompanionCarrier(handle) is not { } carrier) continue;
            if (!physicalTypes.TryGetValue((carrier.PhysicalOwner, carrier.PhysicalOwnerArity), out var matches) ||
                matches.Length != 1)
                throw new InvalidDataException(
                    $"Kotlin companion owner '{carrier.PhysicalOwner}' arity {carrier.PhysicalOwnerArity} " +
                    $"resolved to {(matches is null ? 0 : matches.Length)} physical types");
            var ownerHandle = matches[0];
            _singletonInstanceFields.Add(CompanionCarrierShape.Validate(
                md, _attrs, handle, ownerHandle, carrier.Kind, carrier.PhysicalOwnerArity,
                MetadataTypeName(handle), MetadataTypeName(ownerHandle), carrier.PhysicalOwner));

            // Validate uniqueness before the visibility filter. Two private/internal carrier claims are just as
            // malformed as two public claims; skipping them first would make the projection silently accept an
            // ambiguous compiler-owned association.
            if (!_validatedCompanionOwners.Add(ownerHandle))
                throw new InvalidDataException($"multiple Kotlin companion carriers name owner '{carrier.Owner}'");
            if (!semanticOwners.TryAdd(carrier.Owner, ownerHandle) &&
                semanticOwners[carrier.Owner] != ownerHandle)
                throw new InvalidDataException(
                    $"multiple physical types claim Kotlin companion owner '{carrier.Owner}'");
            // The validated carrier is also the authoritative semantic identity of its public owner. Record that
            // fact even when the association itself is private/internal and therefore source-invisible: a class
            // nested below a generic owner is physically lifted with `$`, and projecting that physical spelling
            // would lose its Kotlin nested identity merely because its own companion is hidden.
            _semanticOwnerNames[ownerHandle] = carrier.Owner;
            // A trusted compiler carrier never represents an ordinary Kotlin nested class. Keep every validated
            // physical TypeDef out of ordinary projection even when its semantic association is private/internal;
            // otherwise a NestedPublic `$Secret` implementation carrier falls through as a raw KLIB declaration.
            _physicalCompanionCarriers.Add(handle);

            // Private/internal associations stay source-invisible. A protected association is visible to subclasses
            // and therefore must be projected; its lifted carrier is a public physical bridge because a subclass of
            // the OUTER is unrelated to that carrier in CLR inheritance. ProjectClass restores protected Kotlin flags.
            if (carrier.Visibility is not ("public" or "protected") ||
                !IsVisibleType(ownerHandle) || !IsVisibleType(handle))
                continue;
            _companionCarriers.Add(handle, carrier);
            if (!_companionsByOwner.TryAdd(ownerHandle, carrier))
                throw new InvalidDataException($"multiple Kotlin companion carriers name owner '{carrier.Owner}'");
            _liftedCompanions.Add(ownerHandle, handle);
        }
    }

    public IReadOnlyList<Fragment> Scan()
    {
        var visible = _md.TypeDefinitions
            .Select(h => (Handle: h, Definition: _md.GetTypeDefinition(h)))
            .Where(x => IsPublicTopLevel(x.Definition))
            .Where(x => !_physicalCompanionCarriers.Contains(x.Handle))
            .Where(x => _md.GetString(x.Definition.Name) != "<Module>")
            .GroupBy(x => _md.GetString(x.Definition.Namespace), StringComparer.Ordinal);

        var result = new List<Fragment>();
        foreach (var package in visible.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var names = new NameTable();
            var fragment = new PackageFragment {
                Package = new Package(),
                IsEmpty = false,
                FqName = package.Key,
            };
            fragment.Package.PackageFqName = names.Package(package.Key);
            var signatures = new SignatureDecoder(
                _md, names, _attrs, _arityNames, _delegateCatalog, _companionCatalog, _innerCatalog,
                SemanticCompanionTypeNames(names));
            var projectedBySemanticName = new Dictionary<string, Class>(StringComparer.Ordinal);

            foreach (var (handle, def) in package.OrderBy(x => _md.GetString(x.Definition.Name), StringComparer.Ordinal))
            {
                if (_attrs.Has(handle, MetadataAttributes.DotKtNs + "KotlinFileClassAttribute"))
                {
                    ReadFileFacade(handle, def, fragment.Package, names, signatures);
                    continue;
                }
                var semanticName = _semanticOwnerNames.GetValueOrDefault(handle);
                var klass = ReadClass(
                    handle,
                    def,
                    names,
                    signatures,
                    semanticClassName: semanticName is null ? null : SemanticClassName(handle, names, semanticName));
                fragment.Class.Add(klass);
                fragment.ClassName.Add(klass.FqName);
                var projectedName = semanticName ?? KotlinFullName(handle);
                if (!projectedBySemanticName.TryAdd(projectedName, klass))
                    throw new InvalidDataException(
                        $"multiple visible CLR types project Kotlin class '{projectedName}'");
                AddCompanion(handle, klass, fragment, names, signatures);
                ReadNestedClasses(handle, klass, fragment, names, signatures);
                ReadCSharpExtensions(handle, def, fragment.Package, names, signatures);
            }
            foreach (var (semanticName, klass) in projectedBySemanticName)
            {
                var parentSeparator = semanticName.LastIndexOf('.');
                if (parentSeparator >= 0 &&
                    projectedBySemanticName.TryGetValue(semanticName[..parentSeparator], out var semanticParent) &&
                    !ReferenceEquals(semanticParent, klass))
                {
                    var childName = semanticName[(parentSeparator + 1)..];
                    if (!semanticParent.NestedClassName.Select(names.StringValue).Contains(childName))
                        semanticParent.NestedClassName.Add(names.String(childName));
                }
            }
            MarkLowPriorityDelegateOverloads(fragment.Package.Function, names);
            fragment.Strings = names.Strings;
            fragment.QualifiedNames = names.QualifiedNames;
            if (fragment.Class.Count != 0 ||
                fragment.Package.Function.Count != 0 ||
                fragment.Package.Property.Count != 0)
                result.Add(new Fragment(package.Key, fragment));
        }
        // C# permits importing an extension through its declaring static type
        // (`import N.Extensions.member`). Kotlin metadata models extensions as
        // top-level declarations, so publish a second, ordinary package
        // fragment named after that container. The function's ClrExternal
        // annotation still carries the one true physical owner.
        foreach (var group in _md.TypeDefinitions
            .Select(h => (Handle: h, Definition: _md.GetTypeDefinition(h)))
            .Where(x => IsPublicTopLevel(x.Definition))
            .Where(x => x.Definition.GetMethods().Any(h =>
                _attrs.Has(h, "System.Runtime.CompilerServices.ExtensionAttribute", requireTrust: false)))
            .GroupBy(x => {
                var ns = _md.GetString(x.Definition.Namespace);
                var simple = _arityNames.Simple(ns, _md.GetString(x.Definition.Name));
                return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
            }, StringComparer.Ordinal))
        {
            var names = new NameTable();
            var package = new Package { PackageFqName = names.Package(group.Key) };
            var fragment = new PackageFragment {
                Package = package,
                IsEmpty = false,
                FqName = group.Key,
            };
            var signatures = new SignatureDecoder(
                _md, names, _attrs, _arityNames, _delegateCatalog, _companionCatalog, _innerCatalog,
                SemanticCompanionTypeNames(names));
            foreach (var (handle, def) in group)
                ReadCSharpExtensions(handle, def, package, names, signatures);
            MarkLowPriorityDelegateOverloads(package.Function, names);
            fragment.Strings = names.Strings;
            fragment.QualifiedNames = names.QualifiedNames;
            if (package.Function.Count != 0)
                result.Add(new Fragment(group.Key, fragment));
        }
        // The KLIB reader probes root_package while resolving ordinary source,
        // even when this assembly has no root-package declarations. A packed
        // KLIB cannot represent an empty directory unless an entry is written,
        // so keep an explicit empty root fragment in every output.
        if (!result.Any(x => x.PackageName.Length == 0))
        {
            var names = new NameTable();
            var fragment = new PackageFragment {
                Package = new Package { PackageFqName = names.Package("") },
                IsEmpty = true,
                FqName = "",
                Strings = names.Strings,
                QualifiedNames = names.QualifiedNames,
            };
            result.Insert(0, new Fragment("", fragment));
        }
        return result;
    }

    private CompanionCarrier? ReadCompanionCarrier(TypeDefinitionHandle handle)
    {
        using var doc = _attrs.CarrierDocument(
            handle, MetadataAttributes.DotKtNs + "KotlinCompanionAttribute");
        if (doc is null) return null;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("kind", out var kindNode) ||
            kindNode.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("name", out var nameNode) ||
            nameNode.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("malformed [KotlinCompanion] carrier: expected kind and name strings");
        var kind = kindNode.GetString()!;
        var name = nameNode.GetString()!;
        if (!CompanionMetadataSyntax.IsCarrierKind(kind) || !CompanionMetadataSyntax.IsSegment(name))
            throw new InvalidDataException(
                "malformed [KotlinCompanion] carrier: invalid kind or semantic name segment");
        string? owner = null;
        if (root.TryGetProperty("owner", out var ownerNode))
        {
            if (ownerNode.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("malformed [KotlinCompanion] carrier: owner must be a string");
            owner = ownerNode.GetString();
        }
        if (owner is null || !CompanionMetadataSyntax.IsQualifiedName(owner))
            throw new InvalidDataException(
                "malformed [KotlinCompanion] carrier: owner must be a non-empty qualified semantic name");
        if (!root.TryGetProperty("physicalOwner", out var physicalOwnerNode) ||
            physicalOwnerNode.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(physicalOwnerNode.GetString()) ||
            !root.TryGetProperty("physicalOwnerArity", out var arityNode) ||
            arityNode.ValueKind != JsonValueKind.Number ||
            !arityNode.TryGetInt32(out var arity) || arity < 0)
            throw new InvalidDataException(
                "malformed [KotlinCompanion] carrier: physicalOwner and physicalOwnerArity are required");
        if (!root.TryGetProperty("visibility", out var visibilityNode) ||
            visibilityNode.ValueKind != JsonValueKind.String ||
            visibilityNode.GetString() is not string visibility ||
            visibility is not ("public" or "internal" or "private" or "protected" or "protectedInternal"))
            throw new InvalidDataException("malformed [KotlinCompanion] carrier: invalid visibility");
        return new CompanionCarrier(kind, owner!, name, visibility, physicalOwnerNode.GetString()!, arity);
    }

    private Class ReadClass(
        TypeDefinitionHandle handle,
        TypeDefinition def,
        NameTable names,
        SignatureDecoder signatures,
        int? semanticClassName = null,
        int? semanticKind = null)
    {
        var metadataName = _md.GetString(def.Name);
        var metadataNamespace = _md.GetString(def.Namespace);
        var kotlinName = KotlinDefinitionPath(handle).Chain[^1];
        var isInterface = (def.Attributes & TypeAttributes.Interface) != 0;
        var isEnum = IsSystemType(def.BaseType, "System", "Enum");
        var isAnnotation = IsAttributeType(handle);
        var isClrExceptionRoot =
            metadataNamespace == "System" &&
            metadataName == "Exception";
        var isObject = _attrs.Has(handle, MetadataAttributes.DotKtNs + "KotlinObjectAttribute");
        var isKotlinSealed = _attrs.Has(handle, MetadataAttributes.DotKtNs + "KotlinSealedAttribute");
        var isKotlinValue = _attrs.Has(handle, MetadataAttributes.DotKtNs + "KotlinValueAttribute");
        var capturedOuterTypeParameters =
            _attrs.Int32(handle, MetadataAttributes.DotKtNs + "KotlinInnerAttribute");
        var isKotlinInner = capturedOuterTypeParameters is not null;
        if (capturedOuterTypeParameters is < 0 || capturedOuterTypeParameters > def.GetGenericParameters().Count)
            throw new InvalidDataException(
                $"Kotlin inner type '{MetadataTypeName(handle)}' carries invalid captured outer parameter count " +
                $"{capturedOuterTypeParameters}");
        var kind = semanticKind ?? (isObject ? 5 : isInterface ? 1 : isEnum ? 2 : isAnnotation ? 4 : 0);
        var modality = isKotlinSealed ? 3
            : kind == 1 || (def.Attributes & TypeAttributes.Abstract) != 0 ? 2
            : (def.Attributes & TypeAttributes.Sealed) == 0 ? 1 : 0;
        var result = new Class {
            FqName = semanticClassName ?? ClassName(handle, names),
            Flags = Flags.Declaration(
                modality,
                kind,
                isValue: isKotlinValue,
                isFun: _attrs.Has(handle, MetadataAttributes.DotKtNs + "KotlinFunInterfaceAttribute"),
                hasEnumEntries: isEnum,
                isInner: isKotlinInner),
        };
        var clrVisibility = def.Attributes & TypeAttributes.VisibilityMask;
        if (clrVisibility is TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem)
            result.Flags = Flags.AsProtected(result.Flags);
        result.ClassAnnotation.Add(ClrExternalAnnotation(names, MetadataTypeName(handle)));
        result.Flags |= 1;

        var typeParameterIds = new Dictionary<GenericParameterHandle, int>();
        var retainedTypeParameters = new Dictionary<GenericParameterHandle, TypeParameter>();
        foreach (var gpHandle in def.GetGenericParameters())
        {
            var gp = _md.GetGenericParameter(gpHandle);
            var id = gp.Index;
            typeParameterIds[gpHandle] = id;
            if (id < capturedOuterTypeParameters.GetValueOrDefault()) continue;
            var parameter = new TypeParameter {
                Id = id,
                Name = names.String(_md.GetString(gp.Name)),
                Variance = (gp.Attributes & GenericParameterAttributes.VarianceMask) switch {
                    GenericParameterAttributes.Covariant => TypeParameter.Types.Variance.Out,
                    GenericParameterAttributes.Contravariant => TypeParameter.Types.Variance.In,
                    _ => TypeParameter.Types.Variance.Inv,
                },
            };
            result.TypeParameter.Add(parameter);
            retainedTypeParameters[gpHandle] = parameter;
        }

        var typeContext = new GenericContext(handle, default, typeParameterIds);
        foreach (var (gpHandle, parameter) in retainedTypeParameters)
        {
            var gp = _md.GetGenericParameter(gpHandle);
            foreach (var constraintHandle in gp.GetConstraints())
            {
                var constraint = _md.GetGenericParameterConstraint(constraintHandle);
                parameter.UpperBound.Add(
                    signatures.DecodeEntity(constraint.Type, typeContext, platform: false));
            }
        }
        if (isKotlinValue)
            AddValueClassRepresentation(handle, def, result, names, signatures, typeContext);
        var accessorPairs = KotlinAccessorPairs(def);
        var customFieldAccessors = CustomFieldAccessors(def);
        var accessorMethods = accessorPairs
            .SelectMany(x => x.Setter.IsNil ? new[] { x.Getter } : new[] { x.Getter, x.Setter })
            .Concat(customFieldAccessors.Values.SelectMany(x => x.Handles))
            .ToHashSet();
        if (isEnum)
        {
            var enumBase = new KType { ClassName = names.Class("kotlin.Enum") };
            var self = new KType { ClassName = result.FqName };
            foreach (var tp in result.TypeParameter)
                self.Argument.Add(new KType.Types.Argument {
                    Projection = KType.Types.Argument.Types.Projection.Inv,
                    Type = new KType { TypeParameter = tp.Id },
                });
            enumBase.Argument.Add(new KType.Types.Argument {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = self,
            });
            result.Supertype.Add(enumBase);
        }
        else if (isAnnotation)
        {
            result.Supertype.Add(signatures.NamedType("kotlin.Annotation"));
        }
        else
        {
            // Kotlin's frontend only permits Throwable subtypes in throw/catch,
            // while ECMA-335 roots the physical CLR exception hierarchy at
            // System.Exception : System.Object. Give that one CLR root its
            // Kotlin vocabulary edge; every CLR exception subclass then
            // inherits Throwable transitively. bir2cir still lowers the
            // declaration itself to System.Exception, so no physical CLR
            // inheritance is invented.
            if (isClrExceptionRoot)
                result.Supertype.Add(signatures.NamedType("kotlin.Throwable"));
            else if (!def.BaseType.IsNil &&
                !IsSystemType(def.BaseType, "System", "Object") &&
                !IsSystemType(def.BaseType, "System", "ValueType") &&
                !IsSystemType(def.BaseType, "System", "Attribute"))
                result.Supertype.Add(signatures.DecodeEntity(def.BaseType, typeContext, platform: false));
            var implementedInterfaces = def.GetInterfaceImplementations()
                .Select(implHandle => {
                    var impl = _md.GetInterfaceImplementation(implHandle);
                    return signatures.DecodeEntity(impl.Interface, typeContext, platform: false);
                })
                .ToList();
            var genericInterfaceNames = implementedInterfaces
                .Where(x => x.Argument.Count != 0 && x.HasClassName)
                .Select(x => names.ClassName(x.ClassName)?.Split('.').Last())
                .Where(x => x is not null)
                .ToHashSet(StringComparer.Ordinal);
            if (isInterface && def.GetGenericParameters().Any())
                genericInterfaceNames.Add(kotlinName);
            foreach (var supertype in implementedInterfaces)
            {
                // Drop the legacy non-generic shadow when the same CLR class
                // implements IList<T>/ICollection<T>/IEnumerable<T>. Exposing
                // both makes Kotlin demand the object-typed explicit-interface
                // slots from subclasses of an otherwise concrete CLR base.
                if (supertype.Argument.Count == 0 &&
                    supertype.HasClassName &&
                    names.ClassName(supertype.ClassName)?.Split('.').Last() is string simple &&
                    genericInterfaceNames.Contains(simple))
                    continue;
                // DotKt emits both IComparable<T> and its non-generic CLR
                // bridge. Only the generic face has Kotlin meaning.
                if (signatures.IsKotlinComparable(supertype) && supertype.Argument.Count == 0)
                    continue;
                result.Supertype.Add(supertype);
            }
            if (result.Supertype.Count == 0)
                result.Supertype.Add(new KType { ClassName = names.Class("kotlin.Any") });
            RestoreErasedSupertypes(handle, result, signatures, names);
        }
        if (isKotlinSealed)
        {
            foreach (var candidateHandle in _md.TypeDefinitions)
            {
                var candidate = _md.GetTypeDefinition(candidateHandle);
                if (!IsVisibleType(candidate) || candidate.BaseType.IsNil) continue;
                var candidateContext = new GenericContext(
                    candidateHandle,
                    default,
                    candidate.GetGenericParameters().ToDictionary(
                        h => h, h => _md.GetGenericParameter(h).Index));
                var baseType = signatures.DecodeEntity(candidate.BaseType, candidateContext, platform: false);
                if (IsSelfType(baseType, result.FqName))
                    result.SealedSubclassFqName.Add(ClassName(candidateHandle, names));
            }
        }

        var projectedFunctions = new List<ProjectedFunction>();
        var projectedConstructors = new List<ProjectedConstructor>();
        foreach (var methodHandle in def.GetMethods())
        {
            var method = _md.GetMethodDefinition(methodHandle);
            // Compiler implementation methods (local functions, state-machine helpers, bridges) are executable CLR
            // details, not Kotlin declarations. Their MethodDefs stay in the assembly but never re-enter the source
            // API on round-trip.
            if (_attrs.IsDotKtAssembly && _attrs.Has(methodHandle,
                    "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
                    requireTrust: false))
                continue;
            if (!IsPublicOrProtected(method.Attributes)) continue;
            var name = _md.GetString(method.Name);
            if (accessorMethods.Contains(methodHandle)) continue;
            var context = new GenericContext(handle, methodHandle, typeParameterIds);
            var sig = method.DecodeSignature(signatures, context);
            if (name == ".ctor")
            {
                var parameters = Parameters(methodHandle, method, sig.ParameterTypes, names, signatures, context)
                    .Skip(isKotlinInner ? 1 : 0);
                var constructor = new Constructor {
                    Flags = Flags.Visibility(method.Attributes),
                    ValueParameter = { parameters },
                };
                result.Constructor.Add(constructor);
                projectedConstructors.Add(new ProjectedConstructor(
                    methodHandle,
                    constructor,
                    PhysicalParameterKeys(method, context)));
            }
            else if ((method.Attributes & MethodAttributes.SpecialName) == 0 && !name.StartsWith('<'))
            {
                var modalityForMethod = (method.Attributes & MethodAttributes.Abstract) != 0 ? 2
                    : (method.Attributes & MethodAttributes.Virtual) != 0 && (method.Attributes & MethodAttributes.Final) == 0 ? 1 : 0;
                var kotlinFlags = _attrs.Int32(methodHandle, MetadataAttributes.DotKtNs + "KotlinFunctionAttribute") ?? 0;
                var isComparableSlot = _attrs.IsDotKtAssembly &&
                    name == "CompareTo" &&
                    sig.ParameterTypes.Length == 1 &&
                    IsSelfType(sig.ParameterTypes[0], result.FqName);
                if (isComparableSlot) kotlinFlags |= 2;
                var function = new Function {
                    Name = names.String(isComparableSlot ? "compareTo" : name),
                    Flags = Flags.Callable(method.Attributes, modalityForMethod,
                        kotlinFlags,
                        isInline: _attrs.Has(methodHandle, MetadataAttributes.DotKtNs + "KotlinInlineAttribute")),
                    ReturnType = ProjectReturn(methodHandle, method, sig.ReturnType, names, signatures, context),
                    ValueParameter = { Parameters(methodHandle, method, sig.ParameterTypes, names, signatures, context) },
                };
                PromoteContextParameters(method, function);
                PromoteReceiver(methodHandle, method, function);
                AddMethodTypeParameters(method, function, names, signatures, context);
                result.Function.Add(function);
                projectedFunctions.Add(new ProjectedFunction(
                    methodHandle,
                    function,
                    PhysicalParameterKeys(method, context)));
            }
        }
        if (!_attrs.IsDotKtAssembly)
        {
            AddNrtParamsOverloadBridges(projectedConstructors, result.Constructor, names);
            AddNrtParamsOverloadBridges(projectedFunctions, result.Function, names);
        }
        AddMemberAwaitBridges(handle, def, result, names, signatures, typeContext);
        foreach (var methodHandle in def.GetMethods())
        {
            var method = _md.GetMethodDefinition(methodHandle);
            var clrName = _md.GetString(method.Name);
            if (!OperatorNames.TryGetValue(clrName, out var operatorName) ||
                !IsPublicOrProtected(method.Attributes) ||
                (method.Attributes & (MethodAttributes.Static | MethodAttributes.SpecialName)) !=
                    (MethodAttributes.Static | MethodAttributes.SpecialName))
                continue;
            var unary = UnaryOperators.Contains(operatorName);
            var context = new GenericContext(handle, methodHandle, typeParameterIds);
            var signature = method.DecodeSignature(signatures, context);
            if (signature.ParameterTypes.Length != (unary ? 1 : 2) ||
                !IsSelfType(signature.ParameterTypes[0], result.FqName))
                continue;
            var parameters = Parameters(
                methodHandle, method, signature.ParameterTypes, names, signatures, context).ToList();
            result.Function.Add(new Function {
                Name = names.String(operatorName),
                Flags = Flags.Callable(method.Attributes, modality: 0, kotlinFlags: 2) & ~(1 << 18),
                ReturnType = ProjectReturn(methodHandle, method, signature.ReturnType, names, signatures, context),
                ValueParameter = { parameters.Skip(1) },
            });
        }

        // A DotKt custom accessor is emitted as an ordinary get_/set_ method pair plus its public storage field.
        // The KLIB declaration must expose the Kotlin property once, routed through those methods; surfacing the field
        // as a second same-name property lets overload resolution select raw storage and bypasses accessor semantics.
        var propertyNames = accessorPairs
            .Select(x => _md.GetString(_md.GetMethodDefinition(x.Getter).Name)[4..])
            .ToHashSet(StringComparer.Ordinal);
        foreach (var propertyHandle in def.GetProperties())
        {
            var property = _md.GetPropertyDefinition(propertyHandle);
            var accessors = property.GetAccessors();
            var getter = accessors.Getter.IsNil ? default(MethodDefinition?) : _md.GetMethodDefinition(accessors.Getter);
            var setter = accessors.Setter.IsNil ? default(MethodDefinition?) : _md.GetMethodDefinition(accessors.Setter);
            if (getter is not { } getMethod && setter is not { } setMethod) continue;
            var representative = getter ?? setter!.Value;
            var metadataPropertyName = _md.GetString(property.Name);
            var explicitInterfaceProperty = metadataPropertyName.Contains('.', StringComparison.Ordinal);
            if (!IsPublicOrProtected(representative.Attributes) && !explicitInterfaceProperty) continue;
            var context = new GenericContext(handle, accessors.Getter.IsNil ? accessors.Setter : accessors.Getter, typeParameterIds);
            var signature = property.DecodeSignature(signatures, context);
            var name = explicitInterfaceProperty
                ? metadataPropertyName[(metadataPropertyName.LastIndexOf('.') + 1)..]
                : metadataPropertyName;
            var canWrite = setter is { } sm &&
                (IsPublicOrProtected(sm.Attributes) || explicitInterfaceProperty);
            var isStatic = (representative.Attributes & MethodAttributes.Static) != 0;
            if (signature.ParameterTypes.Length != 0)
            {
                AddIndexer(result, propertyHandle, property, accessors, representative, signature, canWrite, names, signatures, context);
                continue;
            }
            var propertyType = getter is not null
                ? ProjectReturn(accessors.Getter, getter.Value, signature.ReturnType, names, signatures, context)
                : ProjectType(
                    PhysicalParameters(setter!.Value).Last().Handle,
                    signature.ReturnType,
                    representative.GetDeclaringType(),
                    names,
                    signatures,
                    context);
            var projected = new Property {
                Name = names.String(name),
                ReturnType = propertyType,
                Flags = Flags.Property(
                    explicitInterfaceProperty ? MethodAttributes.Public : representative.Attributes,
                    canWrite,
                    isStatic),
                SetterValueParameter = canWrite
                    ? new ValueParameter { Name = names.String("value"), Type = propertyType.Clone() }
                    : null,
            };
            ApplyAccessorFlags(projected, accessors.Getter, accessors.Setter);
            result.Property.Add(projected);
            propertyNames.Add(name);
        }

        // ECMA MethodImpl rows are the physical form of explicit interface
        // implementations. Kotlin still needs the public interface member on
        // the concrete class so a subclass does not inherit a fictional
        // abstract obligation. Reconstruct accessor pairs generically from
        // their declarations; the actual private body remains a bir2cir
        // binding concern.
        var explicitAccessors = new Dictionary<string, (MethodDefinitionHandle Getter, MethodDefinitionHandle Setter)>(
            StringComparer.Ordinal);
        var explicitFunctions = new List<(string Name, MethodDefinitionHandle Body)>();
        var interfaceKeys = def.GetInterfaceImplementations()
            .Select(h => _md.GetInterfaceImplementation(h).Interface)
            .Select(h => TypeKey(signatures.DecodeEntity(h, typeContext, platform: false)))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var implementationHandle in def.GetMethodImplementations())
        {
            var implementation = _md.GetMethodImplementation(implementationHandle);
            if (implementation.MethodBody.Kind != HandleKind.MethodDefinition) continue;
            if (!IsImplementedInterfaceDeclaration(
                    implementation.MethodDeclaration,
                    interfaceKeys,
                    signatures,
                    typeContext))
                continue;
            var bodyHandle = (MethodDefinitionHandle)implementation.MethodBody;
            var declarationName = implementation.MethodDeclaration.Kind switch {
                HandleKind.MemberReference => _md.GetString(
                    _md.GetMemberReference((MemberReferenceHandle)implementation.MethodDeclaration).Name),
                HandleKind.MethodDefinition => _md.GetString(
                    _md.GetMethodDefinition((MethodDefinitionHandle)implementation.MethodDeclaration).Name),
                _ => "",
            };
            declarationName = SimpleMethodName(declarationName);
            var accessorKind = declarationName.StartsWith("get_", StringComparison.Ordinal) ? 1
                : declarationName.StartsWith("set_", StringComparison.Ordinal) ? 2
                : 0;
            if (accessorKind == 0)
            {
                if (declarationName.Length != 0 &&
                    !declarationName.StartsWith("add_", StringComparison.Ordinal) &&
                    !declarationName.StartsWith("remove_", StringComparison.Ordinal) &&
                    !declarationName.StartsWith("op_", StringComparison.Ordinal))
                    explicitFunctions.Add((declarationName, bodyHandle));
                continue;
            }
            var propertyName = declarationName[4..];
            if (propertyNames.Contains(propertyName)) continue;
            explicitAccessors.TryGetValue(propertyName, out var pair);
            if (accessorKind == 1) pair.Getter = bodyHandle;
            else pair.Setter = bodyHandle;
            explicitAccessors[propertyName] = pair;
        }
        foreach (var (name, pair) in explicitAccessors)
        {
            var representativeHandle = !pair.Getter.IsNil ? pair.Getter : pair.Setter;
            var representative = _md.GetMethodDefinition(representativeHandle);
            var context = new GenericContext(handle, representativeHandle, typeParameterIds);
            KType type;
            if (!pair.Getter.IsNil)
            {
                var getter = _md.GetMethodDefinition(pair.Getter);
                var signature = getter.DecodeSignature(signatures, context with { Method = pair.Getter });
                type = ProjectReturn(
                    pair.Getter,
                    getter,
                    signature.ReturnType,
                    names,
                    signatures,
                    context with { Method = pair.Getter });
            }
            else
            {
                var setter = _md.GetMethodDefinition(pair.Setter);
                var signature = setter.DecodeSignature(signatures, context with { Method = pair.Setter });
                type = ProjectType(
                    PhysicalParameters(setter).Last().Handle,
                    signature.ParameterTypes[^1],
                    handle,
                    names,
                    signatures,
                    context with { Method = pair.Setter });
            }
            result.Property.Add(new Property {
                Name = names.String(name),
                ReturnType = type,
                Flags = Flags.Property(MethodAttributes.Public, !pair.Setter.IsNil, isStatic: false),
                SetterValueParameter = pair.Setter.IsNil
                    ? null
                    : new ValueParameter { Name = names.String("value"), Type = type.Clone() },
            });
            propertyNames.Add(name);
        }

        // A private CLR MethodImpl body satisfies the interface slot, but is
        // intentionally absent from the ordinary public-method scan above.
        // Surface a concrete Kotlin function under the interface declaration's
        // name. Its physical binding remains in bir2cir, which can resolve the
        // class call through the implemented interface slot.
        var functionKeys = result.Function.Select(f => FunctionKey(f, names))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (name, bodyHandle) in explicitFunctions)
        {
            var body = _md.GetMethodDefinition(bodyHandle);
            var context = new GenericContext(handle, bodyHandle, typeParameterIds);
            var signature = body.DecodeSignature(signatures, context);
            var function = new Function {
                Name = names.String(name),
                Flags = Flags.Callable(MethodAttributes.Public, modality: 0),
                ReturnType = ProjectReturn(
                    bodyHandle,
                    body,
                    signature.ReturnType,
                    names,
                    signatures,
                    context),
                ValueParameter = {
                    Parameters(
                        bodyHandle,
                        body,
                        signature.ParameterTypes,
                        names,
                        signatures,
                        context)
                },
            };
            AddMethodTypeParameters(body, function, names, signatures, context);
            if (functionKeys.Add(FunctionKey(function, names)))
                result.Function.Add(function);
        }

        foreach (var fieldHandle in def.GetFields())
        {
            // Suppress the exact ABI singleton slot validated from [KotlinCompanion], not a declaration selected by
            // source name. A companion may legally declare `val INSTANCE: Int`; that property must survive beside
            // the compiler-reserved self slot `$INSTANCE`.
            if (_singletonInstanceFields.Contains(fieldHandle)) continue;
            var field = _md.GetFieldDefinition(fieldHandle);
            if (!IsPublicOrProtected(field.Attributes)) continue;
            var name = _md.GetString(field.Name);
            if (name.StartsWith('<') || propertyNames.Contains(name)) continue;
            if (isEnum && (field.Attributes & FieldAttributes.Literal) != 0 &&
                (field.Attributes & FieldAttributes.Static) != 0)
            {
                result.EnumEntry.Add(new EnumEntry { Name = names.String(name) });
                continue;
            }
            var fieldType = ProjectType(fieldHandle, field.DecodeSignature(signatures, typeContext), handle, names, signatures, typeContext);
            var hasCustomAccessors = customFieldAccessors.TryGetValue(name, out var custom);
            var canWrite = hasCustomAccessors && (custom.Access & 2) != 0 ||
                (field.Attributes & (FieldAttributes.InitOnly | FieldAttributes.Literal)) == 0 &&
                !_attrs.Has(fieldHandle, MetadataAttributes.DotKtNs + "KotlinReadOnlyAttribute");
            var projected = new Property {
                Name = names.String(name),
                ReturnType = fieldType,
                Flags = Flags.Property(field.Attributes, canWrite),
                SetterValueParameter = canWrite
                    ? new ValueParameter { Name = names.String("value"), Type = fieldType.Clone() }
                    : null,
            };
            if ((field.Attributes & FieldAttributes.Literal) != 0 &&
                CompileTimeValue(field, names) is { } constant)
            {
                projected.Flags |= (1 << 11) | (1 << 13); // IS_CONST + HAS_CONSTANT
                projected.CompileTimeValue = constant;
            }
            if (hasCustomAccessors)
                ApplyAccessorFlags(projected, custom.Handles);
            else {
                projected.PropertyAnnotation.Add(new Annotation {
                    Id = names.Class("kotlin.clr.ClrField"),
                });
                projected.Flags |= 1;
            }
            result.Property.Add(projected);
        }

        foreach (var eventHandle in def.GetEvents())
        {
            var ev = _md.GetEventDefinition(eventHandle);
            var accessors = ev.GetAccessors();
            var accessorHandle = !accessors.Adder.IsNil ? accessors.Adder : accessors.Remover;
            if (accessorHandle.IsNil) continue;
            var accessor = _md.GetMethodDefinition(accessorHandle);
            if (!IsPublicOrProtected(accessor.Attributes)) continue;
            var handler = signatures.DecodeEntity(ev.Type, typeContext, platform: false);
            var eventType = signatures.NamedType("kotlin.clr.ClrEvent");
            eventType.Argument.Add(new KType.Types.Argument {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = handler,
            });
            result.Property.Add(new Property {
                Name = names.String(_md.GetString(ev.Name)),
                ReturnType = eventType,
                Flags = Flags.Property(accessor.Attributes, canWrite: false, (accessor.Attributes & MethodAttributes.Static) != 0),
            });
        }
        foreach (var pair in accessorPairs)
            result.Property.Add(KotlinAccessorProperty(handle, pair.Getter, pair.Setter, names, signatures, typeParameterIds));
        AddEnumerableIterator(handle, def, result, names, signatures, typeContext);
        MarkLowPriorityDelegateOverloads(result.Constructor, names);
        MarkLowPriorityDelegateOverloads(result.Function, names);
        if (_companionCarriers.TryGetValue(handle, out var projectedCompanion) &&
            projectedCompanion.Visibility == "protected")
        {
            result.Flags = Flags.AsProtected(result.Flags);
            foreach (var constructor in result.Constructor) constructor.Flags = Flags.AsProtected(constructor.Flags);
            foreach (var function in result.Function) function.Flags = Flags.AsProtected(function.Flags);
            foreach (var property in result.Property)
            {
                property.Flags = Flags.AsProtected(property.Flags);
                if (property.HasGetterFlags) property.GetterFlags = Flags.AsProtected(property.GetterFlags);
                if (property.HasSetterFlags) property.SetterFlags = Flags.AsProtected(property.SetterFlags);
            }
        }
        return result;
    }

    private static string SimpleMethodName(string metadataName)
    {
        var marker = metadataName.LastIndexOf('.');
        return marker < 0 ? metadataName : metadataName[(marker + 1)..];
    }

    private static string TypeKey(KType type) =>
        Convert.ToBase64String(type.ToByteArray());

    private static string FunctionKey(Function function, NameTable names) =>
        names.StringValue(function.Name) + "`" + function.TypeParameter.Count + "(" +
        string.Join(",", function.ValueParameter.Select(p => TypeKey(p.Type))) + ")";

    private bool IsImplementedInterfaceDeclaration(
        EntityHandle declaration,
        HashSet<string> interfaceKeys,
        SignatureDecoder signatures,
        GenericContext context)
    {
        var owner = declaration.Kind switch {
            HandleKind.MemberReference => _md.GetMemberReference((MemberReferenceHandle)declaration).Parent,
            HandleKind.MethodDefinition => _md.GetMethodDefinition((MethodDefinitionHandle)declaration).GetDeclaringType(),
            _ => default,
        };
        if (owner.IsNil ||
            owner.Kind is not (HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification))
            return false;
        return interfaceKeys.Contains(TypeKey(signatures.DecodeEntity(owner, context, platform: false)));
    }

    private void AddMethodTypeParameters(
        MethodDefinition method,
        Function function,
        NameTable names,
        SignatureDecoder signatures,
        GenericContext context)
    {
        foreach (var gpHandle in method.GetGenericParameters())
        {
            var gp = _md.GetGenericParameter(gpHandle);
            var parameter = new TypeParameter {
                Id = 10000 + gp.Index,
                Name = names.String(_md.GetString(gp.Name)),
                Variance = TypeParameter.Types.Variance.Inv,
            };
            foreach (var constraintHandle in gp.GetConstraints())
            {
                var constraint = _md.GetGenericParameterConstraint(constraintHandle);
                parameter.UpperBound.Add(signatures.DecodeEntity(constraint.Type, context, platform: false));
            }
            function.TypeParameter.Add(parameter);
        }
    }

    private void AddValueClassRepresentation(
        TypeDefinitionHandle ownerHandle,
        TypeDefinition owner,
        Class declaration,
        NameTable names,
        SignatureDecoder signatures,
        GenericContext context)
    {
        // [KotlinValue] authenticates this as a DotKt value-class declaration. Its one
        // declared instance field is the underlying value; static fields are unrelated
        // implementation details. Preserve the Kotlin property name independently of
        // constructor visibility/order so FIR need not reconstruct it from a projected
        // primary constructor.
        var instanceFields = owner.GetFields()
            .Select(handle => (Handle: handle, Field: _md.GetFieldDefinition(handle)))
            .Where(x => (x.Field.Attributes & FieldAttributes.Static) == 0)
            .ToArray();
        if (instanceFields.Length != 1) return;

        var (fieldHandle, field) = instanceFields[0];
        var fieldName = _md.GetString(field.Name);
        var propertyName = owner.GetProperties()
            .Select(handle => _md.GetString(_md.GetPropertyDefinition(handle).Name))
            .FirstOrDefault(name =>
                fieldName == name ||
                fieldName == "<" + name + ">k__BackingField")
            ?? fieldName;

        declaration.InlineClassUnderlyingPropertyName = names.String(propertyName);
        declaration.InlineClassUnderlyingType = ProjectType(
            fieldHandle,
            field.DecodeSignature(signatures, context),
            ownerHandle,
            names,
            signatures,
            context);
    }

    private void AddMemberAwaitBridges(
        TypeDefinitionHandle ownerHandle,
        TypeDefinition owner,
        Class declaration,
        NameTable names,
        SignatureDecoder signatures,
        GenericContext ownerContext)
    {
        foreach (var methodHandle in owner.GetMethods())
        {
            var method = _md.GetMethodDefinition(methodHandle);
            if (_md.GetString(method.Name) != "GetAwaiter" ||
                !IsPublicOrProtected(method.Attributes) ||
                (method.Attributes & MethodAttributes.Static) != 0 ||
                method.GetGenericParameters().Count != 0)
                continue;
            var context = ownerContext with { Method = methodHandle };
            var signature = method.DecodeSignature(signatures, context);
            if (signature.ParameterTypes.Length != 0 ||
                !TryAwaitResult(signature.ReturnType, names, signatures, out var resultType))
                continue;
            AddAwaitBridge(
                declaration.Function,
                receiver: null,
                resultType,
                typeParameters: null,
                captureContext: false,
                names,
                signatures);
            if (SupportsConfigureAwait(ownerHandle, names, signatures, ownerContext))
                AddAwaitBridge(
                    declaration.Function,
                    receiver: null,
                    resultType,
                    typeParameters: null,
                    captureContext: true,
                    names,
                    signatures);
            break;
        }
    }

    private bool TryAwaitResult(
        KType awaiterType,
        NameTable names,
        SignatureDecoder signatures,
        out KType resultType)
    {
        resultType = null!;
        if (LocalDefinition(awaiterType, names) is not TypeDefinitionHandle awaiterHandle)
            return false;
        var awaiter = _md.GetTypeDefinition(awaiterHandle);
        var typeParameterIds = awaiter.GetGenericParameters()
            .ToDictionary(h => h, h => _md.GetGenericParameter(h).Index);
        var context = new GenericContext(awaiterHandle, default, typeParameterIds);

        var completed = awaiter.GetProperties().Any(propertyHandle =>
        {
            var property = _md.GetPropertyDefinition(propertyHandle);
            if (_md.GetString(property.Name) != "IsCompleted") return false;
            var getterHandle = property.GetAccessors().Getter;
            if (getterHandle.IsNil) return false;
            var getter = _md.GetMethodDefinition(getterHandle);
            if (!IsPublicOrProtected(getter.Attributes) ||
                (getter.Attributes & MethodAttributes.Static) != 0)
                return false;
            var signature = property.DecodeSignature(
                signatures, context with { Method = getterHandle });
            return signature.ParameterTypes.Length == 0 &&
                IsNamed(signature.ReturnType, "kotlin.Boolean", names);
        });
        if (!completed) return false;

        var onCompleted = awaiter.GetMethods().Any(methodHandle =>
        {
            var method = _md.GetMethodDefinition(methodHandle);
            if (_md.GetString(method.Name) != "OnCompleted" ||
                !IsPublicOrProtected(method.Attributes) ||
                (method.Attributes & MethodAttributes.Static) != 0)
                return false;
            var signature = method.DecodeSignature(
                signatures, context with { Method = methodHandle });
            return signature.ParameterTypes.Length == 1;
        });
        if (!onCompleted) return false;

        foreach (var methodHandle in awaiter.GetMethods())
        {
            var method = _md.GetMethodDefinition(methodHandle);
            if (_md.GetString(method.Name) != "GetResult" ||
                !IsPublicOrProtected(method.Attributes) ||
                (method.Attributes & MethodAttributes.Static) != 0 ||
                method.GetGenericParameters().Count != 0)
                continue;
            var methodContext = context with { Method = methodHandle };
            var signature = method.DecodeSignature(signatures, methodContext);
            if (signature.ParameterTypes.Length != 0) continue;
            var projected = ProjectReturn(
                methodHandle,
                method,
                signature.ReturnType,
                names,
                signatures,
                methodContext);
            resultType = SubstituteTypeParameters(projected, awaiterType.Argument);
            return true;
        }
        return false;
    }

    private bool SupportsConfigureAwait(
        TypeDefinitionHandle awaitableHandle,
        NameTable names,
        SignatureDecoder signatures,
        GenericContext context)
    {
        var awaitable = _md.GetTypeDefinition(awaitableHandle);
        foreach (var methodHandle in awaitable.GetMethods())
        {
            var method = _md.GetMethodDefinition(methodHandle);
            if (_md.GetString(method.Name) != "ConfigureAwait" ||
                !IsPublicOrProtected(method.Attributes) ||
                (method.Attributes & MethodAttributes.Static) != 0 ||
                method.GetGenericParameters().Count != 0)
                continue;
            var methodContext = context with { Method = methodHandle };
            var signature = method.DecodeSignature(signatures, methodContext);
            if (signature.ParameterTypes.Length != 1 ||
                !IsNamed(signature.ParameterTypes[0], "kotlin.Boolean", names))
                continue;
            // This assembly owns only the ConfigureAwait declaration fact. Its returned configured-awaitable may be
            // another type/assembly (and nested TypeRef resolution is intentionally not dependency traversal in the
            // one-DLL worker). Surface the Kotlin overload from the bool-shaped member; bir2cir owns resolving and
            // validating the complete physical configured-awaitable pattern against the full reference universe.
            return true;
        }
        return false;
    }

    private bool SupportsConfigureAwait(
        KType awaitableType,
        NameTable names,
        SignatureDecoder signatures)
    {
        if (LocalDefinition(awaitableType, names) is not TypeDefinitionHandle handle)
            return false;
        var definition = _md.GetTypeDefinition(handle);
        var ids = definition.GetGenericParameters()
            .ToDictionary(h => h, h => _md.GetGenericParameter(h).Index);
        return SupportsConfigureAwait(
            handle,
            names,
            signatures,
            new GenericContext(handle, default, ids));
    }

    private bool TryMemberAwaitResult(
        KType awaitableType,
        NameTable names,
        SignatureDecoder signatures,
        out KType resultType)
    {
        resultType = null!;
        if (LocalDefinition(awaitableType, names) is not TypeDefinitionHandle awaitableHandle)
            return false;
        var awaitable = _md.GetTypeDefinition(awaitableHandle);
        var ids = awaitable.GetGenericParameters()
            .ToDictionary(h => h, h => _md.GetGenericParameter(h).Index);
        var context = new GenericContext(awaitableHandle, default, ids);
        foreach (var methodHandle in awaitable.GetMethods())
        {
            var method = _md.GetMethodDefinition(methodHandle);
            if (_md.GetString(method.Name) != "GetAwaiter" ||
                !IsPublicOrProtected(method.Attributes) ||
                (method.Attributes & MethodAttributes.Static) != 0 ||
                method.GetGenericParameters().Count != 0)
                continue;
            var signature = method.DecodeSignature(
                signatures, context with { Method = methodHandle });
            if (signature.ParameterTypes.Length != 0) continue;
            var concreteAwaiter = SubstituteTypeParameters(
                signature.ReturnType, awaitableType.Argument);
            return TryAwaitResult(concreteAwaiter, names, signatures, out resultType);
        }
        return false;
    }

    private TypeDefinitionHandle? LocalDefinition(KType type, NameTable names)
    {
        if (!type.HasClassName || names.ClassName(type.ClassName) is not string name)
            return null;
        foreach (var handle in _md.TypeDefinitions)
            if (KotlinFullName(handle) == name)
                return handle;
        return null;
    }

    private static KType SubstituteTypeParameters(
        KType source,
        Google.Protobuf.Collections.RepeatedField<KType.Types.Argument> arguments)
    {
        if (source.HasTypeParameter &&
            source.TypeParameter >= 0 &&
            source.TypeParameter < arguments.Count &&
            arguments[source.TypeParameter].Type is { } replacement)
            return replacement.Clone();
        var result = source.Clone();
        for (var i = 0; i < result.Argument.Count; i++)
            if (result.Argument[i].Type is { } argument)
                result.Argument[i].Type = SubstituteTypeParameters(argument, arguments);
        if (result.FlexibleUpperBound is { } upper)
            result.FlexibleUpperBound = SubstituteTypeParameters(upper, arguments);
        return result;
    }

    private static bool IsNamed(KType type, string name, NameTable names) =>
        type.HasClassName && names.ClassName(type.ClassName) == name;

    private static void AddAwaitBridge(
        Google.Protobuf.Collections.RepeatedField<Function> declarations,
        KType? receiver,
        KType resultType,
        IEnumerable<TypeParameter>? typeParameters,
        bool captureContext,
        NameTable names,
        SignatureDecoder signatures)
    {
        var function = new Function {
            Name = names.String("await"),
            Flags = Flags.Callable(
                MethodAttributes.Public,
                modality: 0,
                kotlinFlags: 4),
            ReturnType = resultType.Clone(),
            ReceiverType = receiver is null ? null : signatures.AsNonNull(receiver),
        };
        if (typeParameters is not null)
            function.TypeParameter.Add(typeParameters.Select(x => x.Clone()));
        if (captureContext)
            function.ValueParameter.Add(new ValueParameter {
                Name = names.String("captureContext"),
                Type = signatures.NamedType("kotlin.Boolean"),
            });
        function.FunctionAnnotation.Add(new Annotation {
            Id = names.Class("kotlin.clr.ClrAwaitBridge"),
        });
        function.Flags |= 1;
        declarations.Add(function);
    }

    // Kotlin iteration is a source-level protocol. Derive it from the CLR
    // enumerator pattern itself; no collection-family catalog is involved.
    // ForInLowering owns the later physical GetEnumerator projection, so no
    // CLR-specific marker belongs in KLIB.
    private void AddEnumerableIterator(
        TypeDefinitionHandle handle,
        TypeDefinition def,
        Class result,
        NameTable names,
        SignatureDecoder signatures,
        GenericContext context)
    {
        if (result.Function.Any(f => names.StringValue(f.Name) == "iterator"))
            return;
        var self = new KType { ClassName = ClassName(handle, names) };
        foreach (var parameter in def.GetGenericParameters())
            self.Argument.Add(new KType.Types.Argument {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = new KType {
                    TypeParameter = _md.GetGenericParameter(parameter).Index,
                },
            });
        if (!TryEnumerableElement(
                self,
                names,
                signatures,
                new HashSet<string>(StringComparer.Ordinal),
                out var element))
            return;

        var iterator = signatures.NamedType("kotlin.collections.Iterator");
        iterator.Argument.Add(new KType.Types.Argument {
            Projection = KType.Types.Argument.Types.Projection.Inv,
            Type = element,
        });
        result.Function.Add(new Function {
            Name = names.String("iterator"),
            Flags = Flags.Callable(
                MethodAttributes.Public |
                    ((def.Attributes & TypeAttributes.Interface) != 0
                        ? MethodAttributes.Abstract
                        : 0),
                modality: (def.Attributes & TypeAttributes.Interface) != 0
                    ? 2
                    : (def.Attributes & TypeAttributes.Sealed) == 0 ? 1 : 0,
                kotlinFlags: 2),
            ReturnType = iterator,
        });
    }

    private bool TryEnumerableElement(
        KType enumerableType,
        NameTable names,
        SignatureDecoder signatures,
        HashSet<string> visited,
        out KType elementType)
    {
        elementType = null!;
        if (!enumerableType.HasClassName ||
            names.ClassName(enumerableType.ClassName) is not string identity)
            return false;
        // A referenced interface definition belongs to another input KLIB and is intentionally not embedded here.
        // Its constructed protocol identity still carries the element exactly, so consume that declared fact at the
        // assembly boundary rather than guessing from a collection-family name.
        if (identity == "System.Collections.Generic.IEnumerable" &&
            enumerableType.Argument.Count == 1 &&
            enumerableType.Argument[0].Type is { } protocolElement)
        {
            elementType = protocolElement.Clone();
            return true;
        }
        if (LocalDefinition(enumerableType, names) is not TypeDefinitionHandle handle ||
            !visited.Add(identity))
            return false;
        var definition = _md.GetTypeDefinition(handle);
        var ids = definition.GetGenericParameters()
            .ToDictionary(h => h, h => _md.GetGenericParameter(h).Index);
        var context = new GenericContext(handle, default, ids);

        foreach (var methodHandle in definition.GetMethods())
        {
            var method = _md.GetMethodDefinition(methodHandle);
            if (_md.GetString(method.Name) != "GetEnumerator" ||
                !IsPublicOrProtected(method.Attributes) ||
                (method.Attributes & MethodAttributes.Static) != 0 ||
                method.GetGenericParameters().Count != 0)
                continue;
            var signature = method.DecodeSignature(
                signatures, context with { Method = methodHandle });
            if (signature.ParameterTypes.Length != 0) continue;
            var concreteEnumerator = SubstituteTypeParameters(
                signature.ReturnType, enumerableType.Argument);
            if (TryEnumeratorElement(
                    concreteEnumerator, names, signatures, out elementType))
                return true;
        }

        IEnumerable<EntityHandle> supers = definition.GetInterfaceImplementations()
            .Select(h => _md.GetInterfaceImplementation(h).Interface);
        if (!definition.BaseType.IsNil)
            supers = supers.Append(definition.BaseType);
        foreach (var superHandle in supers)
        {
            var super = signatures.DecodeEntity(superHandle, context, platform: false);
            super = SubstituteTypeParameters(super, enumerableType.Argument);
            if (TryEnumerableElement(super, names, signatures, visited, out elementType))
                return true;
        }
        return false;
    }

    private bool TryEnumeratorElement(
        KType enumeratorType,
        NameTable names,
        SignatureDecoder signatures,
        out KType elementType)
    {
        elementType = null!;
        if (enumeratorType.HasClassName &&
            names.ClassName(enumeratorType.ClassName) == "System.Collections.Generic.IEnumerator" &&
            enumeratorType.Argument.Count == 1 &&
            enumeratorType.Argument[0].Type is { } protocolElement)
        {
            elementType = protocolElement.Clone();
            return true;
        }
        if (LocalDefinition(enumeratorType, names) is not TypeDefinitionHandle handle)
            return false;
        var definition = _md.GetTypeDefinition(handle);
        var ids = definition.GetGenericParameters()
            .ToDictionary(h => h, h => _md.GetGenericParameter(h).Index);
        var context = new GenericContext(handle, default, ids);
        var moves = definition.GetMethods().Any(methodHandle =>
        {
            var method = _md.GetMethodDefinition(methodHandle);
            if (_md.GetString(method.Name) != "MoveNext" ||
                !IsPublicOrProtected(method.Attributes) ||
                (method.Attributes & MethodAttributes.Static) != 0)
                return false;
            var signature = method.DecodeSignature(
                signatures, context with { Method = methodHandle });
            return signature.ParameterTypes.Length == 0 &&
                IsNamed(signature.ReturnType, "kotlin.Boolean", names);
        });
        if (!moves) return false;

        foreach (var propertyHandle in definition.GetProperties())
        {
            var property = _md.GetPropertyDefinition(propertyHandle);
            if (_md.GetString(property.Name) != "Current") continue;
            var getterHandle = property.GetAccessors().Getter;
            if (getterHandle.IsNil) continue;
            var getter = _md.GetMethodDefinition(getterHandle);
            if (!IsPublicOrProtected(getter.Attributes) ||
                (getter.Attributes & MethodAttributes.Static) != 0)
                continue;
            var getterContext = context with { Method = getterHandle };
            var signature = property.DecodeSignature(signatures, getterContext);
            if (signature.ParameterTypes.Length != 0) continue;
            var projected = ProjectReturn(
                getterHandle,
                getter,
                signature.ReturnType,
                names,
                signatures,
                getterContext);
            elementType = SubstituteTypeParameters(
                projected, enumeratorType.Argument);
            return true;
        }
        return false;
    }

    // A foreign CLR overload family can differ in Kotlin ONLY because NRT annotations become real Kotlin
    // nullability. C# erases those annotations while ranking overloads, so
    //
    //   M(string? value)                         (N: fixed arity)
    //   M(string format, params object?[] args)  (V: expanded form)
    //
    // picks N in C# but V in Kotlin: String is strictly more specific than String?, before Kotlin reaches its
    // non-vararg tiebreak. Preserve BOTH real declarations and add a metadata-only, narrowed VIEW of N with V's
    // fixed-prefix Kotlin types. Stock Kotlin resolution then sees equally-specific String parameters and chooses the
    // fixed-arity view. The original nullable view is marked with Kotlin's standard low-priority annotation: it is
    // still the only applicable declaration for a nullable actual, while removing a three-way ambiguity for generic
    // T?/T families when the non-null view applies. The view still names N physically: NRT is absent from the CLR
    // signature, and the raw physical prefix equality below proves there is no Object-vs-String or
    // collapsed-delegate-type substitution hiding here.
    //
    // This is intentionally a CLOSED outer-nullability rule. It never widens a parameter, never looks through nested
    // variance/platform types, and never applies to a DotKt assembly (whose original Kotlin overload semantics must
    // round-trip unchanged). See #367.
    private void AddNrtParamsOverloadBridges(
        IReadOnlyList<ProjectedFunction> methods,
        Google.Protobuf.Collections.RepeatedField<Function> declarations,
        NameTable names)
    {
        var existing = declarations.Select(FunctionSurfaceKey).ToHashSet(StringComparer.Ordinal);
        foreach (var family in methods.GroupBy(x => {
            var method = _md.GetMethodDefinition(x.Handle);
            return (
                Name: _md.GetString(method.Name),
                Static: (method.Attributes & MethodAttributes.Static) != 0,
                GenericArity: method.GetGenericParameters().Count);
        }))
        {
            var variadics = family.Where(x => IsParamsFunction(x.Declaration)).ToList();
            var fixedArity = family.Where(x => x.Declaration.ValueParameter.All(p => p.VarargElementType is null)).ToList();
            foreach (var variadic in variadics)
            foreach (var fixedMethod in fixedArity)
            {
                if (!PhysicalPrefixMatches(fixedMethod.PhysicalParameters, variadic.PhysicalParameters)) continue;
                var fixedTypes = FunctionParameterTypes(fixedMethod.Declaration);
                var variadicTypes = FunctionParameterTypes(variadic.Declaration);
                if (fixedTypes.Count != variadicTypes.Count - 1 ||
                    !IsStrictOuterNullabilityNarrowing(fixedTypes, variadicTypes))
                    continue;

                var bridge = fixedMethod.Declaration.Clone();
                ReplaceFunctionParameterTypes(bridge, variadicTypes.Take(fixedTypes.Count));
                var key = FunctionSurfaceKey(bridge);
                if (!existing.Add(key)) continue;
                declarations.Add(bridge);
                AddLowPriorityAnnotation(fixedMethod.Declaration.FunctionAnnotation, names);
            }
        }
    }

    private static void AddNrtParamsOverloadBridges(
        IReadOnlyList<ProjectedConstructor> constructors,
        Google.Protobuf.Collections.RepeatedField<Constructor> declarations,
        NameTable names)
    {
        var existing = declarations.Select(ConstructorSurfaceKey).ToHashSet(StringComparer.Ordinal);
        var variadics = constructors.Where(x => IsParamsConstructor(x.Declaration)).ToList();
        var fixedArity = constructors.Where(x => x.Declaration.ValueParameter.All(p => p.VarargElementType is null)).ToList();
        foreach (var variadic in variadics)
        foreach (var fixedConstructor in fixedArity)
        {
            if (!PhysicalPrefixMatches(fixedConstructor.PhysicalParameters, variadic.PhysicalParameters)) continue;
            var fixedTypes = fixedConstructor.Declaration.ValueParameter.Select(p => p.Type).ToList();
            var variadicTypes = variadic.Declaration.ValueParameter.Select(p => p.Type).ToList();
            if (fixedTypes.Count != variadicTypes.Count - 1 ||
                !IsStrictOuterNullabilityNarrowing(fixedTypes, variadicTypes))
                continue;

            var bridge = fixedConstructor.Declaration.Clone();
            for (var i = 0; i < fixedTypes.Count; i++)
                bridge.ValueParameter[i].Type = variadicTypes[i].Clone();
            var key = ConstructorSurfaceKey(bridge);
            if (!existing.Add(key)) continue;
            declarations.Add(bridge);
            AddLowPriorityAnnotation(fixedConstructor.Declaration.ConstructorAnnotation, names);
        }
    }

    private static void AddLowPriorityAnnotation(
        Google.Protobuf.Collections.RepeatedField<Annotation> annotations,
        NameTable names)
    {
        if (annotations.Any(a =>
            names.ClassName(a.Id) == "kotlin.internal.LowPriorityInOverloadResolution"))
            return;
        annotations.Add(new Annotation {
            Id = names.Class("kotlin.internal.LowPriorityInOverloadResolution"),
        });
    }

    private ImmutableArray<string> PhysicalParameterKeys(MethodDefinition method, GenericContext context) =>
        method.DecodeSignature(RawSignatureTypeProvider.Instance, context).ParameterTypes;

    private static bool PhysicalPrefixMatches(
        ImmutableArray<string> fixedParameters,
        ImmutableArray<string> variadicParameters) =>
        fixedParameters.Length == variadicParameters.Length - 1 &&
        fixedParameters.SequenceEqual(variadicParameters.Take(fixedParameters.Length), StringComparer.Ordinal);

    private static bool IsParamsFunction(Function function) =>
        function.ValueParameter.Count > 0 &&
        function.ValueParameter[^1].VarargElementType is not null &&
        function.ValueParameter.Take(function.ValueParameter.Count - 1).All(p => p.VarargElementType is null);

    private static bool IsParamsConstructor(Constructor constructor) =>
        constructor.ValueParameter.Count > 0 &&
        constructor.ValueParameter[^1].VarargElementType is not null &&
        constructor.ValueParameter.Take(constructor.ValueParameter.Count - 1).All(p => p.VarargElementType is null);

    private static List<KType> FunctionParameterTypes(Function function)
    {
        var result = new List<KType>();
        if (function.ReceiverType is { } receiver) result.Add(receiver);
        result.AddRange(function.ContextParameter.Select(p => p.Type));
        result.AddRange(function.ValueParameter.Select(p => p.Type));
        return result;
    }

    private static void ReplaceFunctionParameterTypes(Function function, IEnumerable<KType> replacements)
    {
        using var replacement = replacements.GetEnumerator();
        KType Next()
        {
            if (!replacement.MoveNext()) throw new InvalidOperationException("NRT params bridge type-vector mismatch");
            return replacement.Current.Clone();
        }
        if (function.ReceiverType is not null) function.ReceiverType = Next();
        foreach (var parameter in function.ContextParameter) parameter.Type = Next();
        foreach (var parameter in function.ValueParameter) parameter.Type = Next();
        if (replacement.MoveNext()) throw new InvalidOperationException("NRT params bridge type-vector mismatch");
    }

    private static bool IsStrictOuterNullabilityNarrowing(
        IReadOnlyList<KType> wider,
        IReadOnlyList<KType> narrower)
    {
        if (wider.Count > narrower.Count) return false;
        var strict = false;
        for (var i = 0; i < wider.Count; i++)
        {
            if (wider[i].Equals(narrower[i])) continue;
            if (!IsOuterNullabilityNarrowing(wider[i], narrower[i])) return false;
            strict = true;
        }
        return strict;
    }

    private static bool IsOuterNullabilityNarrowing(KType wider, KType narrower)
    {
        // A flexible/platform T! is already considered at both bounds by Kotlin. Replacing an explicit T? with one,
        // or treating its upper bound as a strict NRT contract, would lose information rather than repair an inversion.
        if (!wider.Nullable || narrower.Nullable ||
            wider.FlexibleUpperBound is not null || narrower.FlexibleUpperBound is not null ||
            wider.HasFlexibleTypeCapabilitiesId || narrower.HasFlexibleTypeCapabilitiesId)
            return false;
        var core = wider.Clone();
        core.Nullable = false;
        return core.Equals(narrower);
    }

    private static string FunctionSurfaceKey(Function function) =>
        $"{function.Name}|{function.Flags & (1 << 18)}|{function.TypeParameter.Count}|" +
        string.Join(";", FunctionParameterTypes(function).Select(TypeSurfaceKey));

    private static string ConstructorSurfaceKey(Constructor constructor) =>
        string.Join(";", constructor.ValueParameter.Select(p => TypeSurfaceKey(p.Type)));

    private static string TypeSurfaceKey(KType type) => Convert.ToBase64String(type.ToByteArray());

    // Delegate overload families whose parameter types differ only by
    // function shape are inherently ambiguous for a bare Kotlin lambda.
    // Preserve every overload and express preference with Kotlin's standard
    // annotation.
    private void MarkLowPriorityDelegateOverloads(
        Google.Protobuf.Collections.RepeatedField<Constructor> declarations,
        NameTable names)
    {
        MarkLowPriorityDelegateOverloads(
            declarations,
            _ => "",
            x => x.ValueParameter,
            x => x.ConstructorAnnotation,
            names);
    }

    private void MarkLowPriorityDelegateOverloads(
        Google.Protobuf.Collections.RepeatedField<Function> declarations,
        NameTable names)
    {
        MarkLowPriorityDelegateOverloads(
            declarations,
            x => names.StringValue(x.Name),
            x => x.ValueParameter,
            x => x.FunctionAnnotation,
            names);
    }

    private static void MarkLowPriorityDelegateOverloads<T>(
        IEnumerable<T> declarations,
        Func<T, string> name,
        Func<T, Google.Protobuf.Collections.RepeatedField<ValueParameter>> parameters,
        Func<T, Google.Protobuf.Collections.RepeatedField<Annotation>> annotations,
        NameTable names)
    {
        (bool IsFunction, int Rank) FunctionInfo(KType type)
        {
            if (!type.HasClassName ||
                names.ClassName(type.ClassName) is not string className ||
                !className.StartsWith("kotlin.Function", StringComparison.Ordinal) ||
                type.Argument.Count == 0)
                return (false, 0);
            var arity = Math.Max(0, type.Argument.Count - 1);
            var returnType = type.Argument[^1].Type;
            var returnsUnit = returnType is not null &&
                returnType.HasClassName &&
                names.ClassName(returnType.ClassName) == "kotlin.Unit";
            return (true, arity * 2 + (returnsUnit ? 0 : 1));
        }

        string Shape(T declaration)
        {
            var parts = new List<string>();
            foreach (var parameter in parameters(declaration))
            {
                var info = FunctionInfo(parameter.Type);
                parts.Add(info.IsFunction
                    ? "<function>"
                    : Convert.ToBase64String(parameter.Type.ToByteArray()));
            }
            return name(declaration) + "\0" + string.Join("\0", parts);
        }

        static bool Dominates(int[] preferred, int[] candidate)
        {
            var strict = false;
            for (var i = 0; i < candidate.Length; i++)
            {
                if (preferred[i] > candidate[i]) return false;
                if (preferred[i] < candidate[i]) strict = true;
            }
            return strict;
        }

        foreach (var family in declarations.GroupBy(Shape, StringComparer.Ordinal))
        {
            var members = family.ToList();
            if (members.Count < 2) continue;
            var functionPositions = parameters(members[0])
                .Select((p, i) => (Info: FunctionInfo(p.Type), Index: i))
                .Where(x => x.Info.IsFunction)
                .Select(x => x.Index)
                .ToArray();
            if (functionPositions.Length == 0) continue;
            int[] Ranks(T declaration) => functionPositions
                .Select(i => FunctionInfo(parameters(declaration)[i].Type).Rank)
                .ToArray();
            foreach (var candidate in members)
            {
                var rank = Ranks(candidate);
                if (!members.Any(preferred =>
                    !ReferenceEquals(preferred, candidate) &&
                    Dominates(Ranks(preferred), rank)))
                    continue;
                var annotation = annotations(candidate);
                if (!annotation.Any(a =>
                    names.ClassName(a.Id) == "kotlin.internal.LowPriorityInOverloadResolution"))
                    annotation.Add(new Annotation {
                        Id = names.Class("kotlin.internal.LowPriorityInOverloadResolution"),
                    });
            }
        }
    }

    private bool IsSelfType(KType type, int ownerName)
    {
        if (!type.HasClassName) return false;
        return type.ClassName == ownerName;
    }

    private static readonly Dictionary<string, string> OperatorNames = new(StringComparer.Ordinal) {
        ["op_Addition"] = "plus",
        ["op_Subtraction"] = "minus",
        ["op_Multiply"] = "times",
        ["op_Division"] = "div",
        ["op_Modulus"] = "rem",
        ["op_UnaryNegation"] = "unaryMinus",
        ["op_UnaryPlus"] = "unaryPlus",
        ["op_Increment"] = "inc",
        ["op_Decrement"] = "dec",
    };

    private static readonly HashSet<string> UnaryOperators = new(StringComparer.Ordinal) {
        "unaryMinus", "unaryPlus", "inc", "dec",
    };

    private void ReadCSharpExtensions(
        TypeDefinitionHandle owner,
        TypeDefinition def,
        Package package,
        NameTable names,
        SignatureDecoder signatures)
    {
        var projectedFunctions = new List<ProjectedFunction>();
        foreach (var methodHandle in def.GetMethods())
        {
            var method = _md.GetMethodDefinition(methodHandle);
            if ((method.Attributes & MethodAttributes.Static) == 0 ||
                !IsPublicOrProtected(method.Attributes) ||
                !_attrs.Has(methodHandle, "System.Runtime.CompilerServices.ExtensionAttribute", requireTrust: false))
                continue;
            var typeParameterIds = new Dictionary<GenericParameterHandle, int>();
            var context = new GenericContext(owner, methodHandle, typeParameterIds);
            var sig = method.DecodeSignature(signatures, context);
            var function = new Function {
                Name = names.String(_md.GetString(method.Name)),
                Flags = Flags.Callable(method.Attributes, CallableModality(method.Attributes)) & ~(1 << 18),
                ReturnType = ProjectReturn(methodHandle, method, sig.ReturnType, names, signatures, context),
                ValueParameter = { Parameters(methodHandle, method, sig.ParameterTypes, names, signatures, context) },
            };
            PromoteContextParameters(method, function);
            PromoteReceiver(methodHandle, method, function);
            foreach (var gpHandle in method.GetGenericParameters())
            {
                var gp = _md.GetGenericParameter(gpHandle);
                var tp = new TypeParameter {
                    Id = 10000 + gp.Index,
                    Name = names.String(_md.GetString(gp.Name)),
                    Variance = TypeParameter.Types.Variance.Inv,
                };
                foreach (var cHandle in gp.GetConstraints())
                    tp.UpperBound.Add(signatures.DecodeEntity(_md.GetGenericParameterConstraint(cHandle).Type, context, platform: false));
                function.TypeParameter.Add(tp);
            }
            function.FunctionAnnotation.Add(ClrExternalAnnotation(names, MetadataTypeName(owner)));
            function.Flags |= 1;
            package.Function.Add(function);
            projectedFunctions.Add(new ProjectedFunction(
                methodHandle,
                function,
                PhysicalParameterKeys(method, context)));
            var isGetAwaiter = _md.GetString(method.Name) == "GetAwaiter";
            KType? awaitResult = null;
            var hasAwaitResult = isGetAwaiter &&
                TryAwaitResult(sig.ReturnType, names, signatures, out awaitResult);
            if (isGetAwaiter && Environment.GetEnvironmentVariable("DOTKT_DLL2KLIB_DEBUG_AWAIT") == "1")
                Console.Error.WriteLine(
                    $"dll2klib: await extension {MetadataTypeName(owner)}.{_md.GetString(method.Name)} " +
                    $"params={sig.ParameterTypes.Length} recv={(function.ReceiverType is null ? "no" : "yes")} " +
                    $"awaiter={(sig.ReturnType.HasClassName ? names.ClassName(sig.ReturnType.ClassName) : "<non-class>")} " +
                    $"conforms={hasAwaitResult}");
            if (isGetAwaiter &&
                function.ReceiverType is { } receiver &&
                sig.ParameterTypes.Length == 1 &&
                hasAwaitResult)
            {
                AddAwaitBridge(
                    package.Function,
                    receiver,
                    awaitResult!,
                    function.TypeParameter,
                    captureContext: false,
                    names,
                    signatures);
                if (SupportsConfigureAwait(receiver, names, signatures))
                    AddAwaitBridge(
                        package.Function,
                        receiver,
                        awaitResult!,
                        function.TypeParameter,
                        captureContext: true,
                        names,
                        signatures);
            }
        }
        if (!_attrs.IsDotKtAssembly)
            AddNrtParamsOverloadBridges(projectedFunctions, package.Function, names);
    }

    private void ReadNestedClasses(
        TypeDefinitionHandle parentHandle,
        Class parent,
        PackageFragment fragment,
        NameTable names,
        SignatureDecoder signatures)
    {
        var parentDef = _md.GetTypeDefinition(parentHandle);
        foreach (var childHandle in parentDef.GetNestedTypes())
        {
            if (_physicalCompanionCarriers.Contains(childHandle))
                continue;
            var childDef = _md.GetTypeDefinition(childHandle);
            if (!IsPublicNested(childDef)) continue;
            // CLR nesting is also the physical home of local/anonymous/closure/state-machine implementation
            // types. Their standard generated marker is the declaration boundary: they remain present in the DLL
            // for execution and lexical access, but are not Kotlin classifiers and must not enter NestedClassName.
            if (_attrs.IsDotKtAssembly && _attrs.Has(
                    childHandle,
                    "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
                    requireTrust: false))
                continue;
            var child = ReadClass(childHandle, childDef, names, signatures);
            parent.NestedClassName.Add(names.String(KotlinDefinitionPath(childHandle).Chain[^1]));
            fragment.Class.Add(child);
            fragment.ClassName.Add(child.FqName);
            AddCompanion(childHandle, child, fragment, names, signatures);
            ReadNestedClasses(childHandle, child, fragment, names, signatures);
        }
    }

    private void ReadFileFacade(
        TypeDefinitionHandle handle,
        TypeDefinition def,
        Package package,
        NameTable names,
        SignatureDecoder signatures)
    {
        var typeParameterIds = new Dictionary<GenericParameterHandle, int>();
        var accessorPairs = KotlinAccessorPairs(def, requireStatic: true);
        var customFieldAccessors = CustomFieldAccessors(def, requireStatic: true);
        var extensionPropertyAccessors = accessorPairs
            .SelectMany(x => x.Setter.IsNil ? new[] { x.Getter } : new[] { x.Getter, x.Setter })
            .Concat(customFieldAccessors.Values.SelectMany(x => x.Handles))
            .ToHashSet();
        foreach (var pair in accessorPairs)
        {
            var property = KotlinAccessorProperty(handle, pair.Getter, pair.Setter, names, signatures, typeParameterIds);
            property.PropertyAnnotation.Add(ClrExternalAnnotation(names, MetadataTypeName(handle)));
            property.Flags |= 1;
            package.Property.Add(property);
        }

        foreach (var methodHandle in def.GetMethods())
        {
            var method = _md.GetMethodDefinition(methodHandle);
            if (_attrs.Has(methodHandle,
                    "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
                    requireTrust: false))
                continue;
            var name = _md.GetString(method.Name);
            if (!IsPublicOrProtected(method.Attributes) || name is ".ctor" or ".cctor" ||
                (method.Attributes & MethodAttributes.SpecialName) != 0 || name.StartsWith('<') ||
                extensionPropertyAccessors.Contains(methodHandle))
                continue;
            var context = new GenericContext(handle, methodHandle, typeParameterIds);
            var sig = method.DecodeSignature(signatures, context);
            var modality = (method.Attributes & MethodAttributes.Abstract) != 0 ? 2
                : (method.Attributes & MethodAttributes.Virtual) != 0 && (method.Attributes & MethodAttributes.Final) == 0 ? 1 : 0;
            var function = new Function {
                Name = names.String(name),
                Flags = Flags.Callable(method.Attributes, modality,
                    _attrs.Int32(methodHandle, MetadataAttributes.DotKtNs + "KotlinFunctionAttribute") ?? 0,
                    isInline: _attrs.Has(methodHandle, MetadataAttributes.DotKtNs + "KotlinInlineAttribute")) & ~(1 << 18),
                ReturnType = ProjectReturn(methodHandle, method, sig.ReturnType, names, signatures, context),
                ValueParameter = { Parameters(methodHandle, method, sig.ParameterTypes, names, signatures, context) },
            };
            PromoteContextParameters(method, function);
            PromoteReceiver(methodHandle, method, function);
            foreach (var gpHandle in method.GetGenericParameters())
            {
                var gp = _md.GetGenericParameter(gpHandle);
                var tp = new TypeParameter {
                    Id = 10000 + gp.Index,
                    Name = names.String(_md.GetString(gp.Name)),
                    Variance = TypeParameter.Types.Variance.Inv,
                };
                foreach (var cHandle in gp.GetConstraints())
                    tp.UpperBound.Add(signatures.DecodeEntity(_md.GetGenericParameterConstraint(cHandle).Type, context, platform: false));
                function.TypeParameter.Add(tp);
            }
            function.FunctionAnnotation.Add(ClrExternalAnnotation(names, MetadataTypeName(handle)));
            function.Flags |= 1;
            package.Function.Add(function);
        }

        var propertyNames = accessorPairs
            .Select(x => _md.GetString(_md.GetMethodDefinition(x.Getter).Name)[4..])
            .ToHashSet(StringComparer.Ordinal);
        foreach (var propertyHandle in def.GetProperties())
        {
            var property = _md.GetPropertyDefinition(propertyHandle);
            var accessors = property.GetAccessors();
            var methodHandle = !accessors.Getter.IsNil ? accessors.Getter : accessors.Setter;
            if (methodHandle.IsNil) continue;
            var method = _md.GetMethodDefinition(methodHandle);
            if (!IsPublicOrProtected(method.Attributes)) continue;
            var context = new GenericContext(handle, methodHandle, typeParameterIds);
            var sig = property.DecodeSignature(signatures, context);
            var type = !accessors.Getter.IsNil
                ? ProjectReturn(methodHandle, method, sig.ReturnType, names, signatures, context)
                : ProjectType(
                    PhysicalParameters(method).Last().Handle,
                    sig.ReturnType,
                    handle,
                    names,
                    signatures,
                    context);
            var canWrite = !accessors.Setter.IsNil && IsPublicOrProtected(_md.GetMethodDefinition(accessors.Setter).Attributes);
            var projected = new Property {
                Name = names.String(_md.GetString(property.Name)),
                ReturnType = type,
                Flags = Flags.Property(method.Attributes, canWrite, isStatic: false),
                SetterValueParameter = canWrite ? new ValueParameter { Name = names.String("value"), Type = type.Clone() } : null,
            };
            if (sig.ParameterTypes.Length != 0)
            {
                var receiverHandle = method.GetParameters()
                    .Select(h => (Handle: h, Row: _md.GetParameter(h)))
                    .First(x => x.Row.SequenceNumber == 1).Handle;
                projected.ReceiverType = ProjectType(receiverHandle, sig.ParameterTypes[0], handle, names, signatures, context);
            }
            ApplyAccessorFlags(projected, accessors.Getter, accessors.Setter);
            projected.PropertyAnnotation.Add(ClrExternalAnnotation(names, MetadataTypeName(handle)));
            projected.Flags |= 1;
            package.Property.Add(projected);
            propertyNames.Add(_md.GetString(property.Name));
        }
        foreach (var fieldHandle in def.GetFields())
        {
            var field = _md.GetFieldDefinition(fieldHandle);
            var name = _md.GetString(field.Name);
            if (!IsPublicOrProtected(field.Attributes) || name.StartsWith('<') || propertyNames.Contains(name)) continue;
            var context = new GenericContext(handle, default, typeParameterIds);
            var type = ProjectType(fieldHandle, field.DecodeSignature(signatures, context), handle, names, signatures, context);
            var hasCustomAccessors = customFieldAccessors.TryGetValue(name, out var custom);
            var canWrite = hasCustomAccessors && (custom.Access & 2) != 0 ||
                (field.Attributes & (FieldAttributes.InitOnly | FieldAttributes.Literal)) == 0 &&
                !_attrs.Has(fieldHandle, MetadataAttributes.DotKtNs + "KotlinReadOnlyAttribute");
            var projected = new Property {
                Name = names.String(name),
                ReturnType = type,
                Flags = Flags.Property(field.Attributes, canWrite) & ~(1 << 19),
                SetterValueParameter = canWrite ? new ValueParameter { Name = names.String("value"), Type = type.Clone() } : null,
            };
            if ((field.Attributes & FieldAttributes.Literal) != 0 &&
                CompileTimeValue(field, names) is { } constant)
            {
                projected.Flags |= (1 << 11) | (1 << 13); // IS_CONST + HAS_CONSTANT
                projected.CompileTimeValue = constant;
            }
            projected.PropertyAnnotation.Add(ClrExternalAnnotation(names, MetadataTypeName(handle)));
            if (hasCustomAccessors)
                ApplyAccessorFlags(projected, custom.Handles);
            else {
                projected.PropertyAnnotation.Add(new Annotation {
                    Id = names.Class("kotlin.clr.ClrField"),
                });
            }
            projected.Flags |= 1;
            package.Property.Add(projected);
        }
    }

    private Dictionary<string, (int Access, List<MethodDefinitionHandle> Handles)> CustomFieldAccessors(
        TypeDefinition def,
        bool requireStatic = false)
    {
        if (!_attrs.IsDotKtAssembly) return new(StringComparer.Ordinal);
        var fields = def.GetFields()
            .Select(h => _md.GetString(_md.GetFieldDefinition(h).Name))
            .ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, (int Access, List<MethodDefinitionHandle> Handles)>(StringComparer.Ordinal);
        foreach (var handle in def.GetMethods())
        {
            var method = _md.GetMethodDefinition(handle);
            if ((method.Attributes & MethodAttributes.SpecialName) != 0 ||
                !IsPublicOrProtected(method.Attributes) ||
                requireStatic && (method.Attributes & MethodAttributes.Static) == 0)
                continue;
            var methodName = _md.GetString(method.Name);
            var physical = PhysicalParameters(method);
            var access = methodName.StartsWith("get_", StringComparison.Ordinal) && physical.Count == 0 ? 1
                : methodName.StartsWith("set_", StringComparison.Ordinal) && physical.Count == 1 ? 2
                : 0;
            if (access == 0) continue;
            var propertyName = methodName[4..];
            if (!fields.Contains(propertyName)) continue;
            if (!result.TryGetValue(propertyName, out var existing))
                existing = (0, new List<MethodDefinitionHandle>());
            existing.Access |= access;
            existing.Handles.Add(handle);
            result[propertyName] = existing;
        }
        return result;
    }

    // ECMA-335 literal fields have no runtime storage.  Preserve their value in
    // KLIB's standard compile_time_value extension so FIR treats the projected
    // declaration exactly like a Kotlin const val and inlines it at the use
    // site.  The extension uses Annotation.Argument.Value as its wire format.
    private Annotation.Types.Argument.Types.Value? CompileTimeValue(
        FieldDefinition field,
        NameTable names)
    {
        var handle = field.GetDefaultValue();
        if (handle.IsNil) return null;
        var constant = _md.GetConstant(handle);
        var blob = _md.GetBlobReader(constant.Value);
        var value = new Annotation.Types.Argument.Types.Value();
        switch (constant.TypeCode)
        {
            case ConstantTypeCode.Boolean:
                value.Type = Annotation.Types.Argument.Types.Value.Types.Type.Boolean;
                value.IntValue = blob.ReadBoolean() ? 1 : 0;
                break;
            case ConstantTypeCode.Char:
                value.Type = Annotation.Types.Argument.Types.Value.Types.Type.Char;
                value.IntValue = blob.ReadUInt16();
                break;
            case ConstantTypeCode.SByte:
                value.Type = Annotation.Types.Argument.Types.Value.Types.Type.Byte;
                value.IntValue = blob.ReadSByte();
                break;
            case ConstantTypeCode.Byte:
                value.Type = Annotation.Types.Argument.Types.Value.Types.Type.Byte;
                value.IntValue = blob.ReadByte();
                break;
            case ConstantTypeCode.Int16:
                value.Type = Annotation.Types.Argument.Types.Value.Types.Type.Short;
                value.IntValue = blob.ReadInt16();
                break;
            case ConstantTypeCode.UInt16:
                value.Type = Annotation.Types.Argument.Types.Value.Types.Type.Short;
                value.IntValue = blob.ReadUInt16();
                break;
            case ConstantTypeCode.Int32:
                value.Type = Annotation.Types.Argument.Types.Value.Types.Type.Int;
                value.IntValue = blob.ReadInt32();
                break;
            case ConstantTypeCode.UInt32:
                value.Type = Annotation.Types.Argument.Types.Value.Types.Type.Int;
                value.IntValue = unchecked((int)blob.ReadUInt32());
                break;
            case ConstantTypeCode.Int64:
                value.Type = Annotation.Types.Argument.Types.Value.Types.Type.Long;
                value.IntValue = blob.ReadInt64();
                break;
            case ConstantTypeCode.UInt64:
                value.Type = Annotation.Types.Argument.Types.Value.Types.Type.Long;
                value.IntValue = unchecked((long)blob.ReadUInt64());
                break;
            case ConstantTypeCode.Single:
                value.Type = Annotation.Types.Argument.Types.Value.Types.Type.Float;
                value.FloatValue = blob.ReadSingle();
                break;
            case ConstantTypeCode.Double:
                value.Type = Annotation.Types.Argument.Types.Value.Types.Type.Double;
                value.DoubleValue = blob.ReadDouble();
                break;
            case ConstantTypeCode.String:
                value.Type = Annotation.Types.Argument.Types.Value.Types.Type.String;
                value.StringValue = names.String(blob.ReadUTF16(blob.RemainingBytes));
                break;
            default:
                return null;
        }
        return value;
    }

    private void ApplyAccessorFlags(
        Property property,
        MethodDefinitionHandle getter,
        MethodDefinitionHandle setter)
    {
        if (!getter.IsNil && IsPublicOrProtected(_md.GetMethodDefinition(getter).Attributes))
            property.GetterFlags = Flags.Accessor(_md.GetMethodDefinition(getter).Attributes);
        if (!setter.IsNil && IsPublicOrProtected(_md.GetMethodDefinition(setter).Attributes))
            property.SetterFlags = Flags.Accessor(_md.GetMethodDefinition(setter).Attributes);
    }

    private void ApplyAccessorFlags(
        Property property,
        IEnumerable<MethodDefinitionHandle> accessors)
    {
        foreach (var handle in accessors)
        {
            var method = _md.GetMethodDefinition(handle);
            var name = _md.GetString(method.Name);
            if (name.StartsWith("get_", StringComparison.Ordinal))
                property.GetterFlags = Flags.Accessor(method.Attributes);
            else if (name.StartsWith("set_", StringComparison.Ordinal))
                property.SetterFlags = Flags.Accessor(method.Attributes);
        }
    }

    private void AddCompanion(
        TypeDefinitionHandle ownerHandle,
        Class owner,
        PackageFragment fragment,
        NameTable names,
        SignatureDecoder signatures)
    {
        if (_liftedCompanions.TryGetValue(ownerHandle, out var companionHandle))
        {
            var companionCarrier = _companionCarriers[companionHandle];
            var companionName = names.String(companionCarrier.Name);
            owner.CompanionObjectName = companionName;
            owner.NestedClassName.Add(companionName);
            var companion = ReadClass(
                companionHandle,
                _md.GetTypeDefinition(companionHandle),
                names,
                signatures,
                semanticClassName: CompanionClassName(ownerHandle, names, companionCarrier.Name),
                semanticKind: 6);
            fragment.Class.Add(companion);
            fragment.ClassName.Add(companion.FqName);
            ReadNestedClasses(companionHandle, companion, fragment, names, signatures);
            return;
        }
    }

    private List<(MethodDefinitionHandle Getter, MethodDefinitionHandle Setter)> KotlinAccessorPairs(
        TypeDefinition def,
        bool requireStatic = false)
    {
        var methods = def.GetMethods()
            .Select(h => (Handle: h, Definition: _md.GetMethodDefinition(h)))
            .ToList();
        var result = new List<(MethodDefinitionHandle, MethodDefinitionHandle)>();
        foreach (var (getterHandle, getter) in methods)
        {
            var name = _md.GetString(getter.Name);
            if ((getter.Attributes & MethodAttributes.SpecialName) != 0 ||
                !name.StartsWith("get_", StringComparison.Ordinal) ||
                !IsPublicOrProtected(getter.Attributes) ||
                requireStatic && (getter.Attributes & MethodAttributes.Static) == 0)
                continue;
            var physical = PhysicalParameters(getter);
            var hasReceiver = physical.Count > 0 && !physical[0].Row.Name.IsNil &&
                _md.GetString(physical[0].Row.Name) == "__self";
            var contextStart = hasReceiver ? 1 : 0;
            if (!hasReceiver && physical.Count == 0 ||
                physical.Skip(contextStart).Any(x =>
                    !_attrs.Has(x.Handle, MetadataAttributes.DotKtNs + "KotlinContextParameterAttribute")))
                continue;
            var setter = methods.FirstOrDefault(x =>
                (_md.GetString(x.Definition.Name) == "set_" + name[4..]) &&
                (x.Definition.Attributes & MethodAttributes.SpecialName) == 0 &&
                IsPublicOrProtected(x.Definition.Attributes) &&
                x.Definition.GetParameters().Count == getter.GetParameters().Count + 1);
            result.Add((getterHandle, setter.Handle));
        }
        return result;
    }

    private Property KotlinAccessorProperty(
        TypeDefinitionHandle owner,
        MethodDefinitionHandle getterHandle,
        MethodDefinitionHandle setterHandle,
        NameTable names,
        SignatureDecoder signatures,
        Dictionary<GenericParameterHandle, int> typeParameterIds)
    {
        var getter = _md.GetMethodDefinition(getterHandle);
        var physical = PhysicalParameters(getter);
        var hasReceiver = physical.Count > 0 && !physical[0].Row.Name.IsNil &&
            _md.GetString(physical[0].Row.Name) == "__self";
        var context = new GenericContext(owner, getterHandle, typeParameterIds);
        var signature = getter.DecodeSignature(signatures, context);
        var type = ProjectReturn(getterHandle, getter, signature.ReturnType, names, signatures, context);
        var property = new Property {
            Name = names.String(_md.GetString(getter.Name)[4..]),
            ReturnType = type,
            Flags = Flags.Property(getter.Attributes, !setterHandle.IsNil, isStatic: false),
            SetterValueParameter = setterHandle.IsNil
                ? null
                : new ValueParameter { Name = names.String("value"), Type = type.Clone() },
        };
        ApplyAccessorFlags(property, getterHandle, setterHandle);
        var contextStart = 0;
        if (hasReceiver)
        {
            property.ReceiverType = ProjectType(
                physical[0].Handle, signature.ParameterTypes[0], owner, names, signatures, context);
            contextStart = 1;
        }
        for (var i = contextStart; i < physical.Count; i++)
        {
            property.ContextParameter.Add(new ValueParameter {
                Name = names.String(physical[i].Row.Name.IsNil
                    ? $"context{i - contextStart}"
                    : _md.GetString(physical[i].Row.Name)),
                Type = ProjectType(
                    physical[i].Handle, signature.ParameterTypes[i], owner, names, signatures, context),
            });
        }
        foreach (var gpHandle in getter.GetGenericParameters())
        {
            var gp = _md.GetGenericParameter(gpHandle);
            var parameter = new TypeParameter {
                Id = 10000 + gp.Index,
                Name = names.String(_md.GetString(gp.Name)),
                Variance = TypeParameter.Types.Variance.Inv,
            };
            foreach (var constraintHandle in gp.GetConstraints())
                parameter.UpperBound.Add(signatures.DecodeEntity(
                    _md.GetGenericParameterConstraint(constraintHandle).Type, context, platform: false));
            property.TypeParameter.Add(parameter);
        }
        return property;
    }

    private List<(ParameterHandle Handle, Parameter Row)> PhysicalParameters(MethodDefinition method) =>
        method.GetParameters()
            .Select(h => (Handle: h, Row: _md.GetParameter(h)))
            .Where(x => x.Row.SequenceNumber > 0)
            .OrderBy(x => x.Row.SequenceNumber)
            .ToList();

    private static Annotation ClrExternalAnnotation(NameTable names, string owner)
    {
        var annotation = new Annotation { Id = names.Class("kotlin.clr.ClrExternal") };
        annotation.Argument.Add(new Annotation.Types.Argument {
            NameId = names.String("owner"),
            Value = new Annotation.Types.Argument.Types.Value {
                Type = Annotation.Types.Argument.Types.Value.Types.Type.String,
                StringValue = names.String(owner),
            },
        });
        return annotation;
    }

    private void AddIndexer(
        Class owner,
        PropertyDefinitionHandle propertyHandle,
        PropertyDefinition property,
        PropertyAccessors accessors,
        MethodDefinition representative,
        MethodSignature<KType> propertySignature,
        bool canWrite,
        NameTable names,
        SignatureDecoder signatures,
        GenericContext context)
    {
        if (!accessors.Getter.IsNil)
        {
            var getter = _md.GetMethodDefinition(accessors.Getter);
            if (IsPublicOrProtected(getter.Attributes))
            {
                var sig = getter.DecodeSignature(signatures, context with { Method = accessors.Getter });
                owner.Function.Add(new Function {
                    Name = names.String("get"),
                    Flags = Flags.Callable(getter.Attributes, CallableModality(getter.Attributes), kotlinFlags: 2),
                    ReturnType = ProjectReturn(
                        accessors.Getter,
                        getter,
                        sig.ReturnType,
                        names,
                        signatures,
                        context with { Method = accessors.Getter }),
                    ValueParameter = { Parameters(accessors.Getter, getter, sig.ParameterTypes, names, signatures, context) },
                });
            }
        }
        if (canWrite && !accessors.Setter.IsNil)
        {
            var setter = _md.GetMethodDefinition(accessors.Setter);
            var setterContext = context with { Method = accessors.Setter };
            var sig = setter.DecodeSignature(signatures, setterContext);
            owner.Function.Add(new Function {
                Name = names.String("set"),
                Flags = Flags.Callable(setter.Attributes, CallableModality(setter.Attributes), kotlinFlags: 2),
                ReturnType = signatures.NamedType("kotlin.Unit"),
                ValueParameter = { Parameters(accessors.Setter, setter, sig.ParameterTypes, names, signatures, setterContext) },
            });
        }
    }

    private KType ProjectReturn(
        MethodDefinitionHandle methodHandle,
        MethodDefinition method,
        KType physical,
        NameTable names,
        SignatureDecoder signatures,
        GenericContext context)
    {
        var returnHandle = method.GetParameters()
            .FirstOrDefault(h => _md.GetParameter(h).SequenceNumber == 0);
        if (_attrs.Has(returnHandle, MetadataAttributes.DotKtNs + "KotlinNothingAttribute"))
            return signatures.NamedType("kotlin.Nothing");
        // A CLR ref return is consumed as a Kotlin value by default. The
        // explicit byref(call()) marker retains lvalue identity at the call
        // site; advertising ClrRef<T> as the ordinary return type instead
        // makes delegated `by byref(...)` expose ClrRef<T> as its value.
        if (signatures.ByRefElement(physical) is { } element)
            physical = element;
        var suspend = ((_attrs.Int32(methodHandle, MetadataAttributes.DotKtNs + "KotlinFunctionAttribute") ?? 0) & 4) != 0;
        return ProjectType(
            returnHandle,
            suspend ? signatures.SuspendResult(physical) : physical,
            methodHandle,
            names,
            signatures,
            context,
            flowContract: true,
            nullabilityOffset: suspend ? 1 : 0);
    }

    // RESTORE THE PRE-ERASURE SUPERTYPE EDGES (#86). A supertype argument is a reified argument and erases with the
    // rest — `class E : Sink<Int?>` emits `Sink<object>` — and unlike a member slot the EDGE has no per-slot
    // attribute to carry its Kotlin type. Left erased, a consumer re-imports `E : Sink<Any?>` and `val s:
    // Sink<Int?> = E()` no longer compiles: a Kotlin SOURCE break, which is the one thing an internal
    // representation decision may not spend. The producer therefore states the edges it erased on a type-level
    // `[KotlinSupertypes]` carrier, and they are put back here.
    //
    // Matched by HEAD, not by position. The projection above is not a transcription of the metadata's interface
    // list — it drops the non-generic shadows, collapses the `IComparable` bridge and synthesizes
    // `kotlin.Throwable`/`kotlin.Any` edges — so an index would line up with nothing. Replacing an entry whose class
    // name and argument count the carrier also names keeps every one of those decisions and moves only the
    // arguments, which is all that was erased.
    //
    // A TYPE-PARAMETER BOUND is the same fact one level down and rides the same carrier's `bounds`. `class Box<T :
    // Sink<Int?>>` constrains T to a physical `Sink<object>`, so a consumer that re-derived the bound from the CLR
    // constraint would either weaken it to `Sink<Any?>` or, as it did, publish no bound at all and let
    // `Box<BadSink>` compile and then fail to LOAD. Restored here, the frontend rejects the bad argument where the
    // author wrote it. Keyed by parameter INDEX — a type's own parameter list IS a transcription of the metadata's,
    // unlike its supertype list — and matched by head within the parameter, so a bound the CLR constraint already
    // supplied is replaced rather than duplicated.
    private void RestoreErasedSupertypes(TypeDefinitionHandle handle, Class result, SignatureDecoder signatures,
        NameTable names)
    {
        using var doc = _attrs.CarrierDocument(handle, MetadataAttributes.DotKtNs + "KotlinSupertypesAttribute");
        if (doc is null) return;
        var pre = new List<KType>();
        if (doc.RootElement.TryGetProperty("base", out var b) && TypeNode.Read(b) is { } bn)
            pre.Add(signatures.FromTypeNode(bn));
        if (doc.RootElement.TryGetProperty("interfaces", out var ifs) &&
            ifs.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var i in ifs.EnumerateArray())
                if (TypeNode.Read(i) is { } n) pre.Add(signatures.FromTypeNode(n));
        RestoreErasedBounds(doc, result, signatures, names);
        if (pre.Count == 0) return;
        for (var i = 0; i < result.Supertype.Count; i++)
        {
            var cur = result.Supertype[i];
            if (!cur.HasClassName) continue;
            var curName = names.ClassName(cur.ClassName);
            var match = pre.FirstOrDefault(p => p.HasClassName
                                                && names.ClassName(p.ClassName) == curName
                                                && p.Argument.Count == cur.Argument.Count);
            if (match is not null) result.Supertype[i] = match;
        }
    }

    private static void RestoreErasedBounds(System.Text.Json.JsonDocument doc, Class result,
        SignatureDecoder signatures, NameTable names)
    {
        if (!doc.RootElement.TryGetProperty("bounds", out var bounds) ||
            bounds.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        foreach (var entry in bounds.EnumerateObject())
        {
            if (!int.TryParse(entry.Name, out var index) || entry.Value.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;
            var parameter = result.TypeParameter.FirstOrDefault(p => p.Id == index);
            if (parameter is null) continue;
            foreach (var boundElement in entry.Value.EnumerateArray())
            {
                if (TypeNode.Read(boundElement) is not { } node) continue;
                var bound = signatures.FromTypeNode(node);
                var at = -1;
                if (bound.HasClassName)
                    for (var i = 0; i < parameter.UpperBound.Count && at < 0; i++)
                        if (parameter.UpperBound[i].HasClassName
                            && names.ClassName(parameter.UpperBound[i].ClassName) == names.ClassName(bound.ClassName)
                            && parameter.UpperBound[i].Argument.Count == bound.Argument.Count)
                            at = i;
                if (at >= 0) parameter.UpperBound[at] = bound;
                else parameter.UpperBound.Add(bound);
            }
        }
    }

    private KType ProjectType(
        EntityHandle slot,
        KType physical,
        EntityHandle contextOwner,
        NameTable names,
        SignatureDecoder signatures,
        GenericContext context,
        bool flowContract = false,
        int nullabilityOffset = 0)
    {
        TypeNode? exact = null;
        string? carrierName = null;
        foreach (var carrier in new[] {
            "KotlinTypeAttribute",
            "KotlinSuspendFunctionTypeAttribute",
            "KotlinNullableGenericAttribute",
            "KotlinCollectionIdentityAttribute",
        })
        {
            exact = _attrs.CarrierType(slot, MetadataAttributes.DotKtNs + carrier);
            if (exact is not null)
            {
                carrierName = carrier;
                break;
            }
        }
        var result = exact is null
            ? physical
            : signatures.FromTypeNode(carrierName == "KotlinNullableGenericAttribute"
                ? StripOuterNullability(exact, signatures)
                : exact);
        if (_attrs.Has(slot, MetadataAttributes.DotKtNs + "KotlinExtensionFunctionTypeAttribute"))
            result = signatures.AsExtensionFunction(result);
        if (_attrs.Int32(slot, MetadataAttributes.DotKtNs + "KotlinContextFunctionTypeAttribute") is int contextCount)
            result = signatures.AsContextFunction(result, contextCount);
        var bytes = _attrs.Nullability(slot);
        if (nullabilityOffset != 0 && bytes is { Length: > 0 })
            bytes = bytes.Skip(Math.Min(nullabilityOffset, bytes.Length)).ToArray();
        var contextByte = NullableContext(contextOwner);
        // DotKt signatures are non-null by Kotlin default. Unlike Roslyn,
        // ilemit need not emit a NullableContext(1) row for every declaration;
        // absence therefore means non-null for a trusted DotKt assembly, while
        // it remains oblivious/platform for an ordinary CLR assembly.
        if (_attrs.IsDotKtAssembly && contextByte == 0)
            contextByte = 1;
        result = carrierName switch {
            // This carrier is the exact source type and therefore owns every
            // nullable wrapper in its subtree.
            "KotlinTypeAttribute" => result,
            // These carriers own the function/nested-generic shape while the
            // declaration slot's NRT byte owns its outer nullability. For
            // DotKt, a missing NRT byte is handled above as Kotlin non-null.
            "KotlinSuspendFunctionTypeAttribute" or "KotlinNullableGenericAttribute"
                => signatures.ApplyOuterNullability(result, bytes, contextByte),
            // A collection-identity carrier was captured after reference
            // nullability stripping, so ordinary NRT metadata fills its tree.
            _ => signatures.ApplyNullability(result, bytes, contextByte),
        };
        if (flowContract && _attrs.Has(slot, MetadataAttributes.MaybeNull, requireTrust: false))
            result = signatures.AsPlatform(result);
        else if (flowContract && _attrs.Has(slot, MetadataAttributes.NotNull, requireTrust: false))
            result = signatures.AsNonNull(result);
        return result;
    }

    // The outer `?` of a `[KotlinNullableGeneric]` carrier normally rides the slot's NRT byte, so it is stripped here
    // and re-applied from that byte. A VALUE inner cannot use that channel at all: an NRT byte array describes
    // REFERENCE nodes only, and `Int` contributes none, so a stripped `Int?` comes back as a non-null `Int`. Where the
    // byte cannot carry it, the carrier keeps it — an erasure bridge's `object` slot over a declared `Int?` is exactly
    // that shape.
    private static TypeNode StripOuterNullability(TypeNode type, SignatureDecoder signatures) => type switch {
        TypeNode.Nullable n when signatures.ConsumesOuterNullability(n.Of) => n.Of,
        TypeNode.Oblivious o => o.Of,
        _ => type,
    };

    private byte NullableContext(EntityHandle owner)
    {
        var current = owner;
        while (!current.IsNil)
        {
            if (_attrs.Byte(current, MetadataAttributes.NullableContext, requireTrust: false) is byte b) return b;
            current = current.Kind switch {
                HandleKind.MethodDefinition => _md.GetMethodDefinition((MethodDefinitionHandle)current).GetDeclaringType(),
                HandleKind.TypeDefinition => _md.GetTypeDefinition((TypeDefinitionHandle)current).GetDeclaringType(),
                _ => default,
            };
        }
        return 0;
    }

    private void PromoteReceiver(MethodDefinitionHandle handle, MethodDefinition method, Function function)
    {
        if (function.ValueParameter.Count == 0) return;
        var firstParameter = method.GetParameters()
            .Select(h => (Handle: h, Parameter: _md.GetParameter(h)))
            .Where(x => x.Parameter.SequenceNumber > 0)
            .OrderBy(x => x.Parameter.SequenceNumber)
            .FirstOrDefault(x => !_attrs.Has(x.Handle, MetadataAttributes.DotKtNs + "KotlinContextParameterAttribute"));
        var isReceiver =
            _attrs.Has(handle, "System.Runtime.CompilerServices.ExtensionAttribute", requireTrust: false) ||
            (!firstParameter.Handle.IsNil &&
                !firstParameter.Parameter.Name.IsNil &&
                _md.GetString(firstParameter.Parameter.Name) == "__self");
        if (!isReceiver) return;
        function.ReceiverType = function.ValueParameter[0].Type;
        function.ValueParameter.RemoveAt(0);
    }

    private void PromoteContextParameters(MethodDefinition method, Function function)
    {
        var physical = method.GetParameters()
            .Select(h => (Handle: h, Row: _md.GetParameter(h)))
            .Where(x => x.Row.SequenceNumber > 0)
            .OrderBy(x => x.Row.SequenceNumber)
            .ToList();
        for (var i = physical.Count - 1; i >= 0; i--)
        {
            if (!_attrs.Has(physical[i].Handle, MetadataAttributes.DotKtNs + "KotlinContextParameterAttribute"))
                continue;
            function.ContextParameter.Insert(0, function.ValueParameter[i]);
            function.ValueParameter.RemoveAt(i);
        }
    }

    private int ClassName(TypeDefinitionHandle handle, NameTable names)
    {
        var (package, chain) = KotlinDefinitionPath(handle);
        return names.Class(package, chain);
    }

    private (string Package, IReadOnlyList<string> Chain) KotlinDefinitionPath(TypeDefinitionHandle handle)
    {
        var handles = new Stack<TypeDefinitionHandle>();
        for (var current = handle; !current.IsNil;)
        {
            handles.Push(current);
            current = _md.GetTypeDefinition(current).GetDeclaringType();
        }
        var outer = _md.GetTypeDefinition(handles.Peek());
        var package = _md.GetString(outer.Namespace);
        var rawScope = package;
        var chain = new List<string>();
        foreach (var current in handles)
        {
            var metadataName = _md.GetString(_md.GetTypeDefinition(current).Name);
            chain.Add(_arityNames.Simple(rawScope, metadataName));
            rawScope = string.IsNullOrEmpty(rawScope) ? metadataName : rawScope + "." + metadataName;
        }
        return (package, chain);
    }

    private int CompanionClassName(
        TypeDefinitionHandle handle,
        NameTable names,
        string sourceName = "Companion")
    {
        if (_semanticOwnerNames.TryGetValue(handle, out var semanticOwner))
        {
            var def = _md.GetTypeDefinition(handle);
            while (!def.GetDeclaringType().IsNil)
                def = _md.GetTypeDefinition(def.GetDeclaringType());
            var semanticPackage = _md.GetString(def.Namespace);
            var classPart = string.IsNullOrEmpty(semanticPackage)
                ? semanticOwner
                : semanticOwner.StartsWith(semanticPackage + ".", StringComparison.Ordinal)
                    ? semanticOwner[(semanticPackage.Length + 1)..]
                    : throw new InvalidDataException(
                        $"semantic companion owner '{semanticOwner}' is outside physical package '{semanticPackage}'");
            return names.Class(
                semanticPackage,
                classPart.Split('.', StringSplitOptions.None).Append(sourceName));
        }
        var (package, chain) = KotlinDefinitionPath(handle);
        return names.Class(package, chain.Append(sourceName));
    }

    private IReadOnlyDictionary<TypeDefinitionHandle, int> SemanticCompanionTypeNames(NameTable names) =>
        _liftedCompanions.ToDictionary(
            association => association.Value,
            association => CompanionClassName(
                association.Key, names, _companionCarriers[association.Value].Name));

    private int SemanticClassName(TypeDefinitionHandle handle, NameTable names, string semanticName)
    {
        var current = handle;
        string package = "";
        while (!current.IsNil)
        {
            var def = _md.GetTypeDefinition(current);
            var parent = def.GetDeclaringType();
            if (parent.IsNil) package = _md.GetString(def.Namespace);
            current = parent;
        }
        var classPart = string.IsNullOrEmpty(package)
            ? semanticName
            : semanticName.StartsWith(package + ".", StringComparison.Ordinal)
                ? semanticName[(package.Length + 1)..]
                : throw new InvalidDataException(
                    $"semantic companion owner '{semanticName}' is outside physical package '{package}'");
        var chain = classPart.Split('.', StringSplitOptions.None);
        if (chain.Length == 0)
            throw new InvalidDataException($"semantic companion owner '{semanticName}' has no class name");
        return names.Class(package, chain);
    }

    private string KotlinFullName(TypeDefinitionHandle handle)
    {
        var (package, chain) = KotlinDefinitionPath(handle);
        return string.Join(".", chain.Prepend(package).Where(x => !string.IsNullOrEmpty(x)));
    }

    private static int CallableModality(MethodAttributes attrs) =>
        (attrs & MethodAttributes.Abstract) != 0 ? 2
        : (attrs & MethodAttributes.Virtual) != 0 && (attrs & MethodAttributes.Final) == 0 ? 1 : 0;

    private IEnumerable<ValueParameter> Parameters(
        MethodDefinitionHandle methodHandle,
        MethodDefinition method,
        ImmutableArray<KType> types,
        NameTable names,
        SignatureDecoder signatures,
        GenericContext context)
    {
        var rows = method.GetParameters().Select(h => (Handle: h, Row: _md.GetParameter(h)))
            .Where(p => p.Row.SequenceNumber > 0).ToDictionary(p => p.Row.SequenceNumber);
        for (var i = 0; i < types.Length; i++)
        {
            if (!rows.TryGetValue(i + 1, out var entry))
            {
                // ECMA-335 permits a signature parameter without a Param row.
                // Synthesized delegates emitted by ilemit use that compact
                // form. The signature is authoritative; only the optional
                // name/attributes are absent.
                yield return new ValueParameter {
                    Name = names.String($"arg{i}"),
                    Type = ProjectType(default(EntityHandle), types[i], methodHandle, names, signatures, context),
                };
                continue;
            }
            var row = entry.Row;
            var name = row.Name.IsNil ? $"arg{i}" : _md.GetString(row.Name);
            var projected = ProjectType(entry.Handle, types[i], methodHandle, names, signatures, context);
            var flags = (row.Attributes & (ParameterAttributes.Optional | ParameterAttributes.HasDefault)) != 0 ||
                _attrs.Has(entry.Handle, "kotlin.clr.KotlinDefault", requireTrust: false) ? 1 << 1 : 0;
            var value = new ValueParameter {
                Name = names.String(string.IsNullOrEmpty(name) ? $"arg{i}" : name),
                Type = projected,
                Flags = flags,
            };
            if (_attrs.Has(entry.Handle, "System.ParamArrayAttribute", requireTrust: false) &&
                signatures.ArrayElement(projected) is { } element)
                value.VarargElementType = element;
            yield return value;
        }
    }

    private string FullName(TypeDefinition def, string? simpleName = null)
    {
        var ns = _md.GetString(def.Namespace);
        var name = simpleName ?? _md.GetString(def.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private string MetadataTypeName(TypeDefinitionHandle handle)
    {
        var chain = new Stack<string>();
        var current = handle;
        string package = "";
        while (!current.IsNil)
        {
            var def = _md.GetTypeDefinition(current);
            // The owner annotation carries the CLR identity stem; generic arity is already represented by the KLIB
            // classifier's type parameters and the BIR TypeNode arguments. Retaining `` `N`` here makes ilemit append
            // arity a second time (`Signal`1`1), so keep only the metadata-name stem.
            chain.Push(StripArity(_md.GetString(def.Name)));
            var parent = def.GetDeclaringType();
            if (parent.IsNil) package = _md.GetString(def.Namespace);
            current = parent;
        }
        var name = string.Join("+", chain);
        return string.IsNullOrEmpty(package) ? name : package + "." + name;
    }

    private static bool IsPublicTopLevel(TypeDefinition def) =>
        (def.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public;
    private static bool IsPublicNested(TypeDefinition def) =>
        (def.Attributes & TypeAttributes.VisibilityMask) is
            TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem;
    private bool IsVisibleType(TypeDefinitionHandle handle)
    {
        var def = _md.GetTypeDefinition(handle);
        var parent = def.GetDeclaringType();
        return parent.IsNil ? IsPublicTopLevel(def) : IsPublicNested(def) && IsVisibleType(parent);
    }
    private static bool IsVisibleType(TypeDefinition def) =>
        IsPublicTopLevel(def) || IsPublicNested(def);
    private static bool IsPublicOrProtected(MethodAttributes attrs) =>
        (attrs & MethodAttributes.MemberAccessMask) is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem;
    private static bool IsPublicOrProtected(FieldAttributes attrs) =>
        (attrs & FieldAttributes.FieldAccessMask) is FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem;
    private bool IsSystemType(EntityHandle handle, string ns, string name)
    {
        if (handle.IsNil) return false;
        return handle.Kind switch {
            HandleKind.TypeReference => IsReference(_md.GetTypeReference((TypeReferenceHandle)handle)),
            HandleKind.TypeDefinition => IsDefinition(_md.GetTypeDefinition((TypeDefinitionHandle)handle)),
            _ => false,
        };
        bool IsReference(TypeReference t) => _md.GetString(t.Namespace) == ns && _md.GetString(t.Name) == name;
        bool IsDefinition(TypeDefinition t) => _md.GetString(t.Namespace) == ns && _md.GetString(t.Name) == name;
    }

    private bool IsAttributeType(TypeDefinitionHandle handle)
    {
        var seen = new HashSet<TypeDefinitionHandle>();
        var current = handle;
        while (!current.IsNil && seen.Add(current))
        {
            var baseType = _md.GetTypeDefinition(current).BaseType;
            if (IsSystemType(baseType, "System", "Attribute")) return true;
            if (baseType.Kind != HandleKind.TypeDefinition) return false;
            current = (TypeDefinitionHandle)baseType;
        }
        return false;
    }
    private static string StripArity(string name) => name.Contains('`') ? name[..name.IndexOf('`')] : name;
}

internal static class Flags
{
    // metadata.proto: hasAnnotations(1), visibility(3), modality(2), then class kind/member kind.
    public static int Declaration(int modality, int kind, bool isValue = false, bool isFun = false,
        bool hasEnumEntries = false, bool isInner = false) =>
        6 | (modality << 4) | (kind << 6)
        | (isInner ? 1 << 9 : 0) | (isValue ? 1 << 13 : 0) | (isFun ? 1 << 14 : 0)
        | (hasEnumEntries ? 1 << 15 : 0);
    public static int Callable(MethodAttributes attrs, int modality, int kotlinFlags = 0, bool isInline = false) =>
        Visibility(attrs) | (modality << 4)
        | ((kotlinFlags & 2) != 0 ? 1 << 8 : 0)
        | ((kotlinFlags & 1) != 0 ? 1 << 9 : 0)
        | (isInline ? 1 << 10 : 0)
        | ((kotlinFlags & 4) != 0 ? 1 << 13 : 0)
        // Kotlin 2.4 metadata Flags.IS_STATIC_FUNCTION. This is a frontend fact
        // present in ECMA-335 MethodAttributes, not a CLR call-shape decision.
        | ((attrs & MethodAttributes.Static) != 0 ? 1 << 18 : 0);
    public static int Visibility(MethodAttributes attrs) =>
        (attrs & MethodAttributes.MemberAccessMask) == MethodAttributes.Public ? 6 : 4; // PUBLIC=3, PROTECTED=2
    public static int AsProtected(int flags) => (flags & ~0xE) | 4;
    public static int Property(MethodAttributes attrs, bool canWrite, bool isStatic) =>
        Visibility(attrs) | (((attrs & MethodAttributes.Abstract) != 0 ? 2
            : (attrs & MethodAttributes.Virtual) != 0 && (attrs & MethodAttributes.Final) == 0 ? 1 : 0) << 4)
        | (canWrite ? 1 << 8 : 0) | 1 << 9 | (canWrite ? 1 << 10 : 0)
        | (isStatic ? 1 << 19 : 0);
    public static int Property(FieldAttributes attrs, bool canWrite) =>
        ((attrs & FieldAttributes.FieldAccessMask) == FieldAttributes.Public ? 6 : 4)
        | (canWrite ? 1 << 8 : 0) | 1 << 9 | (canWrite ? 1 << 10 : 0)
        | ((attrs & FieldAttributes.Static) != 0 ? 1 << 19 : 0);
    // metadata.proto accessor flags: declaration prefix + IS_NOT_DEFAULT.
    // dll2klib only writes an accessor record when an actual CLR accessor
    // method exists; an omitted record is the standard default-accessor form.
    public static int Accessor(MethodAttributes attrs) =>
        Visibility(attrs)
        | (((attrs & MethodAttributes.Abstract) != 0 ? 2
            : (attrs & MethodAttributes.Virtual) != 0 && (attrs & MethodAttributes.Final) == 0 ? 1 : 0) << 4)
        | 1 << 6;
}

internal sealed class NameTable
{
    private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Parent, int Short, QualifiedNameTable.Types.QualifiedName.Types.Kind Kind), int> _qualified = new();
    private readonly Dictionary<int, string> _classNames = new();
    public StringTable Strings { get; } = new();
    public QualifiedNameTable QualifiedNames { get; } = new();

    public int String(string value)
    {
        if (_strings.TryGetValue(value, out var id)) return id;
        id = Strings.String.Count;
        Strings.String.Add(value);
        _strings.Add(value, id);
        return id;
    }

    public int Package(string fqName)
    {
        var parent = -1;
        if (string.IsNullOrEmpty(fqName)) return parent;
        foreach (var part in fqName.Split('.'))
            parent = Qualified(parent, String(part), QualifiedNameTable.Types.QualifiedName.Types.Kind.Package);
        return parent;
    }

    public int Class(string fqName)
    {
        var dot = fqName.LastIndexOf('.');
        var package = dot < 0 ? "" : fqName[..dot];
        var simple = dot < 0 ? fqName : fqName[(dot + 1)..];
        var id = Qualified(Package(package), String(simple), QualifiedNameTable.Types.QualifiedName.Types.Kind.Class);
        _classNames[id] = fqName;
        return id;
    }

    public int Class(string package, IEnumerable<string> nestedNames)
    {
        var parent = Package(package);
        var full = package;
        foreach (var name in nestedNames)
        {
            parent = Qualified(parent, String(name), QualifiedNameTable.Types.QualifiedName.Types.Kind.Class);
            full = string.IsNullOrEmpty(full) ? name : full + "." + name;
            _classNames[parent] = full;
        }
        return parent;
    }

    public string? ClassName(int id) => _classNames.GetValueOrDefault(id);
    public string StringValue(int id) => Strings.String[id];

    private int Qualified(int parent, int shortName, QualifiedNameTable.Types.QualifiedName.Types.Kind kind)
    {
        var key = (parent, shortName, kind);
        if (_qualified.TryGetValue(key, out var id)) return id;
        id = QualifiedNames.QualifiedName.Count;
        QualifiedNames.QualifiedName.Add(new QualifiedNameTable.Types.QualifiedName {
            ParentQualifiedName = parent,
            ShortName = shortName,
            Kind = kind,
        });
        _qualified.Add(key, id);
        return id;
    }
}

internal sealed record GenericContext(
    TypeDefinitionHandle Type,
    MethodDefinitionHandle Method,
    IReadOnlyDictionary<GenericParameterHandle, int> TypeParameterIds);

// A lossless-enough identity decoder for comparing two parameter TYPE signatures inside one CLR owner. The ordinary
// SignatureDecoder deliberately projects distinct CLR types into shared Kotlin vocabulary (two delegate classes with
// the same Invoke shape, for example), so its KType output cannot prove that an NRT bridge still targets the same
// physical member. This decoder retains CLR names, class/value kind, generic positions, arrays, pointers, byrefs,
// function pointers, and custom modifiers. A false negative merely omits a bridge; a false positive could retarget a
// call, so the comparison is intentionally strict.
internal sealed class RawSignatureTypeProvider : ISignatureTypeProvider<string, GenericContext>
{
    public static RawSignatureTypeProvider Instance { get; } = new();

    public string GetArrayType(string elementType, ArrayShape shape) =>
        $"array[{shape.Rank};{string.Join(",", shape.Sizes)};{string.Join(",", shape.LowerBounds)}]<{elementType}>";
    public string GetByReferenceType(string elementType) => $"byref<{elementType}>";
    public string GetFunctionPointerType(MethodSignature<string> signature) =>
        $"fnptr[{signature.Header.RawValue};{signature.GenericParameterCount};{signature.RequiredParameterCount}]" +
        $"({string.Join(",", signature.ParameterTypes)})->{signature.ReturnType}";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        $"{genericType}<{string.Join(",", typeArguments)}>";
    public string GetGenericMethodParameter(GenericContext genericContext, int index) => $"!!{index}";
    public string GetGenericTypeParameter(GenericContext genericContext, int index) => $"!{index}";
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
        $"{(isRequired ? "modreq" : "modopt")}<{modifier}>({unmodifiedType})";
    public string GetPinnedType(string elementType) => $"pinned<{elementType}>";
    public string GetPointerType(string elementType) => $"ptr<{elementType}>";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => $"primitive:{(int)typeCode}";
    public string GetSZArrayType(string elementType) => $"szarray<{elementType}>";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        $"{rawTypeKind}:def:{DefinitionName(reader, handle)}";
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        $"{rawTypeKind}:ref:{ReferenceName(reader, handle)}";
    public string GetTypeFromSpecification(
        MetadataReader reader,
        GenericContext genericContext,
        TypeSpecificationHandle handle,
        byte rawTypeKind) =>
        $"{rawTypeKind}:spec:{reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext)}";

    private static string DefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var simple = reader.GetString(definition.Name);
        var parent = definition.GetDeclaringType();
        if (!parent.IsNil) return DefinitionName(reader, parent) + "+" + simple;
        var ns = reader.GetString(definition.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    private static string ReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var simple = reader.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            return ReferenceName(reader, (TypeReferenceHandle)reference.ResolutionScope) + "+" + simple;
        var ns = reader.GetString(reference.Namespace);
        var name = string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
        var scope = reference.ResolutionScope.Kind switch {
            HandleKind.AssemblyReference =>
                "asm:" + reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name),
            HandleKind.ModuleReference =>
                "module:" + reader.GetString(reader.GetModuleReference((ModuleReferenceHandle)reference.ResolutionScope).Name),
            HandleKind.ModuleDefinition => "module:self",
            _ => reference.ResolutionScope.Kind.ToString(),
        };
        return scope + ":" + name;
    }
}

internal sealed class SignatureDecoder : ISignatureTypeProvider<KType, GenericContext>
{
    private readonly MetadataReader _md;
    private readonly NameTable _names;
    private readonly MetadataAttributes _attrs;
    private readonly ArityNames _arityNames;
    private readonly DelegateReferenceCatalog _delegateCatalog;
    private readonly CompanionReferenceCatalog _companionCatalog;
    private readonly InnerReferenceCatalog _innerCatalog;
    private readonly IReadOnlyDictionary<TypeDefinitionHandle, int> _semanticTypeNames;
    private readonly bool _restoreKotlinCollections;
    private readonly Dictionary<string, TypeDefinitionHandle> _delegateDefinitions = new(StringComparer.Ordinal);
    private readonly Dictionary<KType, DelegateCatalogEntry> _externalDelegateTypes =
        new(ReferenceEqualityComparer.Instance);
    // A physical nested companion under a generic CLR owner repeats the owner's type slots on its TypeDef, but its
    // restored Kotlin companion classifier declares no type parameters. Remember the exact open KType object emitted
    // by GetTypeFromDefinition/Reference so GetGenericInstantiation can consume, rather than leak, those physical
    // capture arguments into the semantic KLIB type.
    private readonly HashSet<KType> _semanticCompanionTypes = new(ReferenceEqualityComparer.Instance);
    // A trusted DotKt inner TypeDef re-declares its enclosing CLR generic slots as a leading physical prefix. They are
    // not Kotlin type arguments of the inner classifier; consume that prefix when a signature constructs the type.
    private readonly Dictionary<KType, int[]> _semanticInnerTypes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, KType> _externalDelegateShapes = new(StringComparer.Ordinal);
    // The struct-ness oracle for the NRT byte walk, keyed by the PROJECTED name a KType carries. An ECMA signature
    // states value-ness at every occurrence (`ELEMENT_TYPE_VALUETYPE` vs `ELEMENT_TYPE_CLASS`, the `rawTypeKind` the
    // provider callbacks receive), so no cross-assembly resolution is needed: a name observed once as a value type is
    // one everywhere. Seeded with this assembly's own struct/enum definitions and with the Kotlin primitives, so a
    // carrier-built type (which never passes a rawTypeKind) is answered too.
    private readonly HashSet<string> _valueTypeNames = new(StringComparer.Ordinal);

    public SignatureDecoder(
        MetadataReader md,
        NameTable names,
        MetadataAttributes attrs,
        ArityNames arityNames,
        DelegateReferenceCatalog delegateCatalog,
        CompanionReferenceCatalog companionCatalog,
        InnerReferenceCatalog innerCatalog,
        IReadOnlyDictionary<TypeDefinitionHandle, int>? semanticTypeNames = null)
    {
        _md = md;
        _names = names;
        _attrs = attrs;
        _arityNames = arityNames;
        _delegateCatalog = delegateCatalog;
        _companionCatalog = companionCatalog;
        _innerCatalog = innerCatalog;
        _semanticTypeNames = semanticTypeNames ?? new Dictionary<TypeDefinitionHandle, int>();
        _restoreKotlinCollections = attrs.IsDotKtAssembly;
        // The Kotlin primitives, plus `kotlin.Unit`: it is the name ECMA `void` decodes to, so it holds no NRT byte and
        // takes no annotation — a rule bir2cir's writer implements from the other side ([NullableFlags]), which is what
        // keeps the two ends counting the same positions. (csc would give the `Unit` CLASS a byte like any reference;
        // this is a stated DotKt deviation, recorded in docs/dotkt-semantics.md § 9.)
        foreach (var name in new[] {
            "kotlin.Unit", "kotlin.Boolean", "kotlin.Char", "kotlin.Byte", "kotlin.UByte",
            "kotlin.Short", "kotlin.UShort", "kotlin.Int", "kotlin.UInt",
            "kotlin.Long", "kotlin.ULong", "kotlin.Float", "kotlin.Double",
        }) _valueTypeNames.Add(name);
        foreach (var handle in md.TypeDefinitions)
        {
            var def = md.GetTypeDefinition(handle);
            if (IsSystemType(def.BaseType, "System", "MulticastDelegate"))
                _delegateDefinitions[DefinitionKotlinName(md, handle)] = handle;
            else if (IsSystemType(def.BaseType, "System", "ValueType") || IsSystemType(def.BaseType, "System", "Enum"))
                _valueTypeNames.Add(_arityNames.Full(md.GetString(def.Namespace), md.GetString(def.Name)));
        }
    }

    // Record `type`'s projected name as a value type when — and only when — the signature occurrence STATES
    // `ELEMENT_TYPE_VALUETYPE`. Never on "not Class": SRM passes `SignatureTypeKind.Unknown` for a CUSTOM MODIFIER's
    // type, and a modifier names a REFERENCE marker class (`InAttribute`, `IsExternalInit`, `CallConv*`) — recording
    // one would make that name value-ish for every later occurrence, in an assembly-global name-keyed set.
    private KType MarkValueTypeIfStated(byte rawTypeKind, KType type) =>
        rawTypeKind == (byte)SignatureTypeKind.ValueType ? MarkValueType(type) : type;

    // Record `type`'s projected name as a value type, AFTER any rename (`System.Span` -> `kotlin.clr.Span`), so the
    // recorded key is the one the NRT walk will look up.
    private KType MarkValueType(KType type)
    {
        if (type.HasClassName && _names.ClassName(type.ClassName) is { } name) _valueTypeNames.Add(name);
        return type;
    }

    private bool IsValueTypeName(string? name) => name is not null && _valueTypeNames.Contains(name);

    public KType GetArrayType(KType elementType, ArrayShape shape) => Array(elementType);
    public KType GetByReferenceType(KType elementType) => ByRef(elementType);
    public KType GetFunctionPointerType(MethodSignature<KType> signature) => Any(nullable: true);
    public KType GetGenericInstantiation(KType genericType, ImmutableArray<KType> typeArguments)
    {
        if (_semanticCompanionTypes.Contains(genericType)) return genericType;
        if (_externalDelegateTypes.TryGetValue(genericType, out var externalDelegate))
            return Substitute(DecodeExternalDelegate(externalDelegate), typeArguments);
        var genericName = genericType.HasClassName ? _names.ClassName(genericType.ClassName) : null;
        if (genericName is "System.Nullable" or "System.Nullable1" &&
            typeArguments.Length == 1)
            return Nullable(typeArguments[0]);
        if (genericName == "System.Span" && typeArguments.Length == 1)
        {
            var span = Named("kotlin.clr.Span");
            span.Argument.Add(new KType.Types.Argument {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = typeArguments[0].Clone(),
            });
            return MarkValueType(span);
        }
        if (genericName is not null && IsKnownDelegate(genericName))
            return ConstructDelegate(genericName, typeArguments);
        // CLR nested TypeSpecs flatten an inner class as [outer capture..., own...]. Kotlin metadata flattens the
        // same classifier as [own..., outer...]; preserve every argument and rotate at this representation boundary.
        IEnumerable<KType> semanticTypeArguments = typeArguments;
        if (_semanticInnerTypes.TryGetValue(genericType, out var semanticArgumentOrder))
        {
            if (semanticArgumentOrder.Length != typeArguments.Length)
                throw new InvalidDataException(
                    $"Kotlin inner type generic shape expects {semanticArgumentOrder.Length} CLR arguments, " +
                    $"but its signature supplies {typeArguments.Length}");
            semanticTypeArguments = semanticArgumentOrder.Select(index => typeArguments[index]);
        }
        var copy = genericType.Clone();
        copy.Argument.Add(semanticTypeArguments.Select(t => new KType.Types.Argument {
            Projection = KType.Types.Argument.Types.Projection.Inv,
            Type = t,
        }));
        if (copy.FlexibleUpperBound is { } upper)
            upper.Argument.Add(semanticTypeArguments.Select(t => new KType.Types.Argument {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = t.Clone(),
            }));
        return copy;
    }
    public KType GetGenericMethodParameter(GenericContext genericContext, int index) => new() { TypeParameter = 10000 + index };
    public KType GetGenericTypeParameter(GenericContext genericContext, int index) => new() { TypeParameter = index };
    public KType GetModifiedType(KType modifier, KType unmodifiedType, bool isRequired) => unmodifiedType;
    public KType GetPinnedType(KType elementType) => elementType;
    public KType GetPointerType(KType elementType) => Any(nullable: true);
    public KType GetPrimitiveType(PrimitiveTypeCode code) => code switch {
        PrimitiveTypeCode.Void => Named("kotlin.Unit"),
        PrimitiveTypeCode.Boolean => Named("kotlin.Boolean"),
        PrimitiveTypeCode.Char => Named("kotlin.Char"),
        PrimitiveTypeCode.SByte => Named("kotlin.Byte"),
        PrimitiveTypeCode.Byte => Named("kotlin.UByte"),
        PrimitiveTypeCode.Int16 => Named("kotlin.Short"),
        PrimitiveTypeCode.UInt16 => Named("kotlin.UShort"),
        PrimitiveTypeCode.Int32 => Named("kotlin.Int"),
        PrimitiveTypeCode.UInt32 => Named("kotlin.UInt"),
        PrimitiveTypeCode.Int64 => Named("kotlin.Long"),
        PrimitiveTypeCode.UInt64 => Named("kotlin.ULong"),
        PrimitiveTypeCode.Single => Named("kotlin.Float"),
        PrimitiveTypeCode.Double => Named("kotlin.Double"),
        PrimitiveTypeCode.String => Platform("kotlin.String"),
        PrimitiveTypeCode.Object => Platform("kotlin.Any"),
        _ => Any(nullable: true),
    };
    public KType GetSZArrayType(KType elementType) => Array(elementType);

    (string Package, IReadOnlyList<string> Names) DefinitionKotlinPath(
        MetadataReader reader, TypeDefinitionHandle handle)
    {
        var handles = new Stack<TypeDefinitionHandle>();
        for (var current = handle; !current.IsNil;)
        {
            handles.Push(current);
            current = reader.GetTypeDefinition(current).GetDeclaringType();
        }
        var package = reader.GetString(reader.GetTypeDefinition(handles.Peek()).Namespace);
        var rawScope = package;
        var names = new List<string>();
        foreach (var current in handles)
        {
            var metadataName = reader.GetString(reader.GetTypeDefinition(current).Name);
            names.Add(_arityNames.Simple(rawScope, metadataName));
            rawScope = string.IsNullOrEmpty(rawScope) ? metadataName : rawScope + "." + metadataName;
        }
        return (package, names);
    }

    string DefinitionKotlinName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var (package, names) = DefinitionKotlinPath(reader, handle);
        return string.Join(".", names.Prepend(package).Where(x => !string.IsNullOrEmpty(x)));
    }

    int DefinitionKotlinClassName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var (package, names) = DefinitionKotlinPath(reader, handle);
        return _names.Class(package, names);
    }

    (string Package, IReadOnlyList<string> Names) ReferenceKotlinPath(
        MetadataReader reader, TypeReferenceHandle handle)
    {
        var metadataNames = new Stack<string>();
        var current = handle;
        var package = "";
        while (!current.IsNil)
        {
            var reference = reader.GetTypeReference(current);
            metadataNames.Push(reader.GetString(reference.Name));
            if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
                current = (TypeReferenceHandle)reference.ResolutionScope;
            else
            {
                package = reader.GetString(reference.Namespace);
                break;
            }
        }
        var rawScope = package;
        var names = new List<string>();
        foreach (var metadataName in metadataNames)
        {
            names.Add(_arityNames.Simple(rawScope, metadataName));
            rawScope = string.IsNullOrEmpty(rawScope) ? metadataName : rawScope + "." + metadataName;
        }
        return (package, names);
    }

    string ReferenceKotlinName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var (package, names) = ReferenceKotlinPath(reader, handle);
        return string.Join(".", names.Prepend(package).Where(x => !string.IsNullOrEmpty(x)));
    }

    int ReferenceKotlinClassName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var (package, names) = ReferenceKotlinPath(reader, handle);
        return _names.Class(package, names);
    }

    string ReferenceMetadataName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var names = new Stack<string>();
        var current = handle;
        var package = "";
        while (!current.IsNil)
        {
            var reference = reader.GetTypeReference(current);
            names.Push(StripArity(reader.GetString(reference.Name)));
            if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
                current = (TypeReferenceHandle)reference.ResolutionScope;
            else
            {
                package = reader.GetString(reference.Namespace);
                break;
            }
        }
        return string.Join(".", names.Prepend(package).Where(x => !string.IsNullOrEmpty(x)));
    }

    public KType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var def = reader.GetTypeDefinition(handle);
        var name = DefinitionKotlinName(reader, handle);
        var className = DefinitionKotlinClassName(reader, handle);
        if (_semanticTypeNames.TryGetValue(handle, out var semanticName))
        {
            var semantic = rawTypeKind == (byte)SignatureTypeKind.Class
                ? Platform(semanticName)
                : MarkValueTypeIfStated(rawTypeKind, Named(semanticName));
            _semanticCompanionTypes.Add(semantic);
            return semantic;
        }
        if (_attrs.CarrierType(handle, MetadataAttributes.DotKtNs + "KotlinTypeAttribute") is { } carrier)
            return FromTypeNode(carrier);
        if (_delegateDefinitions.ContainsKey(name) && !def.GetGenericParameters().Any())
            return DecodeDelegate(handle);
        var result = rawTypeKind == (byte)SignatureTypeKind.Class
            ? Platform(className)
            : MarkValueTypeIfStated(rawTypeKind, Named(className));
        if (_attrs.Int32(handle, MetadataAttributes.DotKtNs + "KotlinInnerAttribute") is not null)
            _semanticInnerTypes[result] = InnerReferenceCatalog.SemanticArgumentOrder(reader, handle);
        return result;
    }
    public KType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        if (_companionCatalog.TryResolve(reader, handle, out var externalCompanion))
        {
            var semantic = rawTypeKind == (byte)SignatureTypeKind.Class
                ? ExternalCompanion(externalCompanion, platform: true)
                : MarkValueTypeIfStated(rawTypeKind, ExternalCompanion(externalCompanion, platform: false));
            _semanticCompanionTypes.Add(semantic);
            return semantic;
        }
        var type = reader.GetTypeReference(handle);
        var metadataName = reader.GetString(type.Name);
        var ns = reader.GetString(type.Namespace);
        var full = ReferenceKotlinName(reader, handle);
        var className = ReferenceKotlinClassName(reader, handle);
        var metadataFull = ReferenceMetadataName(reader, handle);
        if (_delegateCatalog.TryResolve(reader, handle, out var externalDelegate))
        {
            if (!metadataName.Contains('`'))
                return DecodeExternalDelegate(externalDelegate);
            var marker = rawTypeKind == (byte)SignatureTypeKind.Class
                ? Platform(className)
                : Named(className);
            _externalDelegateTypes[marker] = externalDelegate;
            return marker;
        }
        if (_innerCatalog.TryResolve(reader, handle, out var externalInner))
        {
            var marker = rawTypeKind == (byte)SignatureTypeKind.Class
                ? Platform(className)
                : MarkValueTypeIfStated(rawTypeKind, Named(className));
            _semanticInnerTypes[marker] = externalInner.SemanticArgumentOrder;
            return marker;
        }
        if (_restoreKotlinCollections && KotlinCollection(full) is string collection)
            return Platform(collection);
        if (_restoreKotlinCollections && metadataFull == "System.IComparable")
            return Named("kotlin.Comparable");
        // A generic signature is decoded in two callbacks: first its open
        // TypeRef, then GetGenericInstantiation. Do not prematurely turn
        // Action`N/EventHandler`1 into Function0 here or the later arguments
        // would merely be appended to the wrong Function0 constructor.
        if (full is "System.Action" or "System.EventHandler" && !metadataName.Contains('`'))
            return KnownDelegate(full, ImmutableArray<KType>.Empty);
        return full switch {
            "System.String" => Platform("kotlin.String"),
            "System.Object" => Platform("kotlin.Any"),
            _ => rawTypeKind == (byte)SignatureTypeKind.Class ? Platform(className) : MarkValueTypeIfStated(rawTypeKind, Named(className)),
        };
    }
    public KType GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public KType DecodeEntity(EntityHandle handle, GenericContext context, bool platform) =>
        handle.Kind switch {
            HandleKind.TypeDefinition => FromDefinition((TypeDefinitionHandle)handle, platform),
            HandleKind.TypeReference => FromReference((TypeReferenceHandle)handle, platform),
            // Signature decoding derives flexible class types from the raw
            // ECMA signature kind. That is right for an ordinary member slot,
            // but illegal for a declared base/interface edge: a Kotlin
            // supertype must be rigid. Preserve generic arguments and clear
            // only the outer platform wrapper requested by DecodeEntity.
            HandleKind.TypeSpecification => platform
                ? _md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, context)
                : AsNonNull(_md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, context)),
            _ => Any(nullable: true),
        };

    public KType NamedType(string fqName, bool nullable = false) => Named(fqName, nullable);

    public bool IsKotlinComparable(KType type) =>
        type.HasClassName && _names.ClassName(type.ClassName) == "kotlin.Comparable";

    public KType SuspendResult(KType physical)
    {
        if (physical.HasClassName &&
            _names.ClassName(physical.ClassName) is "System.Threading.Tasks.Task" or "System.Threading.Tasks.Task1")
        {
            if (physical.Argument.Count == 1 && physical.Argument[0].Type is { } result)
                return result.Clone();
            return Named("kotlin.Unit");
        }
        return physical;
    }

    public KType? ArrayElement(KType array)
    {
        if (!array.HasClassName) return null;
        var name = _names.ClassName(array.ClassName);
        if (name == "kotlin.Array" &&
            array.Argument.Count == 1 &&
            array.Argument[0].Type is { } element)
            return element.Clone();
        var elementName = name switch {
            "kotlin.BooleanArray" => "kotlin.Boolean",
            "kotlin.CharArray" => "kotlin.Char",
            "kotlin.ByteArray" => "kotlin.Byte",
            "kotlin.UByteArray" => "kotlin.UByte",
            "kotlin.ShortArray" => "kotlin.Short",
            "kotlin.UShortArray" => "kotlin.UShort",
            "kotlin.IntArray" => "kotlin.Int",
            "kotlin.UIntArray" => "kotlin.UInt",
            "kotlin.LongArray" => "kotlin.Long",
            "kotlin.ULongArray" => "kotlin.ULong",
            "kotlin.FloatArray" => "kotlin.Float",
            "kotlin.DoubleArray" => "kotlin.Double",
            _ => null,
        };
        return elementName is null ? null : Named(elementName);
    }

    public KType? ByRefElement(KType type) =>
        type.HasClassName &&
        _names.ClassName(type.ClassName) == "kotlin.clr.ClrRef" &&
        type.Argument.Count == 1
            ? type.Argument[0].Type?.Clone()
            : null;

    public KType FromTypeNode(TypeNode node) => node switch
    {
        TypeNode.Fqn f => FromFqn(f),
        TypeNode.Tv v => new KType { TypeParameter = v.Scope == "method" ? 10000 + v.I : v.I },
        TypeNode.Star => StarType(),
        TypeNode.Fn f => FromFunction(f),
        TypeNode.Nullable n => AsNullable(FromTypeNode(n.Of)),
        TypeNode.Oblivious o => AsPlatform(FromTypeNode(o.Of)),
        TypeNode.Array a => Array(FromTypeNode(a.Elem)),
        TypeNode.ByRef b => ByRef(FromTypeNode(b.Of)),
        _ => Any(nullable: true),
    };

    // Fold a declaration slot's NRT metadata over a decoded type, walking the tree in the SAME pre-order the emitting
    // compiler flattened it in (see [ConsumesNullability] for which nodes hold a byte). A node that holds no byte still
    // has its arguments walked, because the flattening does: `Dictionary<int, string?>` is `[1, 2]`, not `[1]`.
    public KType ApplyNullability(KType source, byte[]? flags, byte context)
    {
        var index = 0;
        return Walk(source);

        KType Walk(KType type)
        {
            var copy = type.Clone();
            var consumes = ConsumesNullability(copy);
            var byteValue = consumes
                ? flags is { Length: 1 } ? flags[0]
                : flags is not null && index < flags.Length ? flags[index]
                : context
                : (byte)1;
            if (consumes) index++;
            for (var i = 0; i < copy.Argument.Count; i++)
                if (copy.Argument[i].Type is { } arg)
                    copy.Argument[i].Type = Walk(arg);
            // A VALUE type holds a byte position but never an annotation: its byte is always 0 (oblivious), and its
            // one nullable form is the structural `System.Nullable<T>` already folded in by the decoder. Reading the
            // byte — or, worse, the enclosing declaration's `[NullableContext]` — as an annotation is what turned
            // `String.Compare(String, String, StringComparison)` into a call taking `StringComparison?`.
            if (consumes && !IsValueType(copy))
                copy = byteValue switch {
                    0 => AsPlatform(copy),
                    2 => AsNullable(copy),
                    _ => AsNonNull(copy),
                };
            return copy;
        }
    }

    public KType AsPlatform(KType source)
    {
        var lower = AsNonNull(source);
        lower.FlexibleTypeCapabilitiesId = _names.String("dotkt.clr.PlatformType");
        lower.FlexibleUpperBound = AsNullable(lower);
        lower.FlexibleUpperBound.ClearFlexibleTypeCapabilitiesId();
        lower.FlexibleUpperBound.FlexibleUpperBound = null;
        return lower;
    }

    public KType AsNonNull(KType source)
    {
        var copy = source.Clone();
        copy.Nullable = false;
        copy.ClearFlexibleTypeCapabilitiesId();
        copy.FlexibleUpperBound = null;
        return copy;
    }

    public KType AsExtensionFunction(KType source)
    {
        var copy = source.Clone();
        copy.TypeAnnotation.Add(new Annotation { Id = _names.Class("kotlin.ExtensionFunctionType") });
        return copy;
    }

    public KType AsContextFunction(KType source, int count)
    {
        var copy = source.Clone();
        var annotation = new Annotation { Id = _names.Class("kotlin.ContextFunctionTypeParams") };
        annotation.Argument.Add(new Annotation.Types.Argument {
            NameId = _names.String("count"),
            Value = new Annotation.Types.Argument.Types.Value {
                Type = Annotation.Types.Argument.Types.Value.Types.Type.Int,
                IntValue = count,
            },
        });
        copy.TypeAnnotation.Add(annotation);
        return copy;
    }

    public KType ApplyOuterNullability(KType source, byte[]? flags, byte context)
    {
        if (!ConsumesNullability(source) || IsValueType(source)) return source;
        var value = flags is { Length: > 0 } ? flags[0] : context;
        return value switch {
            0 => AsPlatform(source),
            2 => AsNullable(source),
            _ => AsNonNull(source),
        };
    }

    private KType AsNullable(KType source)
    {
        var copy = AsNonNull(source);
        copy.Nullable = true;
        return copy;
    }

    private KType FromFqn(TypeNode.Fqn f)
    {
        var name = NormalizeKotlinName(f.Name);
        var type = Named(name);
        if (f.Args is not null)
            type.Argument.Add(f.Args.Select(a => a is TypeNode.Star
                ? new KType.Types.Argument { Projection = KType.Types.Argument.Types.Projection.Star }
                : new KType.Types.Argument {
                    Projection = KType.Types.Argument.Types.Projection.Inv,
                    Type = FromTypeNode(a),
                }));
        return type;
    }

    private KType FromFunction(TypeNode.Fn function)
    {
        var parameters = new List<TypeNode>();
        if (function.Ctx is not null) parameters.AddRange(function.Ctx);
        if (function.Recv is not null) parameters.Add(function.Recv);
        parameters.AddRange(function.Params);
        // Kotlin metadata serializes a suspend function type as its runtime
        // FunctionN shape (logical params + Continuation<R> -> Any?) plus the
        // Type.SUSPEND flag. The deserializer reconstructs the source-level
        // suspend function from that shape.
        var result = Named($"kotlin.Function{parameters.Count + (function.Suspend ? 1 : 0)}");
        result.Flags = function.Suspend ? 1 : 0;
        if (function.Recv is not null)
            result.TypeAnnotation.Add(new Annotation { Id = _names.Class("kotlin.ExtensionFunctionType") });
        if (function.Ctx is { Length: > 0 })
            result = AsContextFunction(result, function.Ctx.Length);
        foreach (var parameter in parameters)
            result.Argument.Add(new KType.Types.Argument {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = FromTypeNode(parameter),
            });
        if (function.Suspend)
        {
            var continuation = Named("kotlin.coroutines.Continuation");
            continuation.Argument.Add(new KType.Types.Argument {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = FromTypeNode(function.Ret),
            });
            result.Argument.Add(new KType.Types.Argument {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = continuation,
            });
            result.Argument.Add(new KType.Types.Argument {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = Any(nullable: true),
            });
        }
        else
        {
            result.Argument.Add(new KType.Types.Argument {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = FromTypeNode(function.Ret),
            });
        }
        return result;
    }

    private KType StarType() => Any(nullable: true);

    private KType ByRef(KType element)
    {
        var result = Named("kotlin.clr.ClrRef");
        result.Argument.Add(new KType.Types.Argument {
            Projection = KType.Types.Argument.Types.Projection.Inv,
            Type = element,
        });
        return result;
    }

    // The same question asked of a carrier's TypeNode, before it becomes a KType: can this type's `?` ride an NRT
    // byte? Answered by the SAME predicate the walk uses, so [StripOuterNullability] never removes a `?` that
    // [ApplyOuterNullability] then declines to put back.
    public bool ConsumesOuterNullability(TypeNode type) => type switch
    {
        // A CONSTRUCTED value type holds a byte but is never annotated by it, so the bare `Named` here is the right
        // question for BOTH: can this node's `?` ride the byte at all.
        TypeNode.Fqn f => ConsumesNullability(Named(NormalizeKotlinName(f.Name))),
        TypeNode.ByRef => false,
        _ => true,
    };

    // Does this node hold a byte in the flattened NRT array? The flattening is the emitting compiler's, and it is
    // measurable rather than a matter of taste — `Dictionary<int, string?>` is `[1, 2]`, `Dictionary<E, string?>`
    // (E an enum) is `[1, 2]`, `Dictionary<E[], string?>` is `[1, 1, 2]`, `KeyValuePair<string?, int>` is `[0, 2]`,
    // `Dictionary<string, KeyValuePair<string?, int>>` is `[1, 1, 0, 2]`. So:
    //   * a reference type and a type parameter hold a byte;
    //   * a value type holds one only when it is CONSTRUCTED (has type arguments) — and it is always 0, never an
    //     annotation (see the guard in [ApplyNullability]). A bare struct, enum or primitive holds none;
    //   * `System.Nullable<T>` and a byref are transparent, holding none — the decoder has already folded the former
    //     into the referent's `?` and surfaced the latter as `ClrRef<T>`.
    // Getting this wrong does not merely mis-annotate the node: every later byte in the same slot shifts.
    //
    // The positions the DECODER collapses to `kotlin.Any?` are counted as one reference node each, which measures out
    // differently per shape (all against csc, `<Nullable>enable</Nullable>`):
    //   * a native `nint`/`nuint`/`IntPtr` holds NO byte (`Dictionary<nint, string?>` is `[1, 2]`,
    //     `delegate*<nint, string?, void>` is `[0, 2]`) while one is consumed here — a SHIFT of every later byte;
    //   * a function POINTER flattens its own node plus its return and parameters (`delegate*<string?, string?>` is
    //     `[0, 2, 2]`) while one is consumed here — a SHIFT;
    //   * an ordinary pointer holds exactly one byte (`delegate*<int*, string?, void>` is `[0, 0, 2]`), so the count
    //     agrees and only the projected SHAPE is lost — and the node it lands on is already `kotlin.Any?`;
    //   * a `where T : struct` parameter holds one byte valued 0 (`Dictionary<T, string?>` is `[1, 0, 2]`), which is
    //     the byte a type parameter consumes here anyway: no shift, and the 0 reads back as a platform type.
    // A shift is always slot-local, never cross-parameter — the byte array is per declaration slot.
    private bool ConsumesNullability(KType type)
    {
        if (type.HasTypeParameter) return true;
        if (!type.HasClassName) return false;
        var name = _names.ClassName(type.ClassName);
        if (name == "kotlin.clr.ClrRef") return false;
        return !IsValueTypeName(name) || type.Argument.Count > 0;
    }

    // Is this node a CLR value type? Its NRT byte, when it holds one, is a placeholder rather than an annotation.
    private bool IsValueType(KType type) =>
        !type.HasTypeParameter && type.HasClassName && IsValueTypeName(_names.ClassName(type.ClassName));

    private static string NormalizeKotlinName(string name) => name switch
    {
        "Unit" => "kotlin.Unit",
        "Boolean" => "kotlin.Boolean",
        "Char" => "kotlin.Char",
        "Byte" => "kotlin.Byte",
        "UByte" => "kotlin.UByte",
        "Short" => "kotlin.Short",
        "UShort" => "kotlin.UShort",
        "Int" => "kotlin.Int",
        "UInt" => "kotlin.UInt",
        "Long" => "kotlin.Long",
        "ULong" => "kotlin.ULong",
        "Float" => "kotlin.Float",
        "Double" => "kotlin.Double",
        "String" => "kotlin.String",
        "Any" => "kotlin.Any",
        "Nothing" => "kotlin.Nothing",
        // Kotlin declaration identities never carry ECMA-335's metadata-name arity suffix. KotlinType carriers are
        // allowed to mention nested generic types, so remove every `N segment rather than only the final simple name.
        _ => StripGenericArities(name),
    };

    private static string StripGenericArities(string name)
    {
        var result = name;
        var search = 0;
        while ((search = result.IndexOf('`', search)) >= 0)
        {
            var end = search + 1;
            while (end < result.Length && char.IsDigit(result[end])) end++;
            result = result.Remove(search, end - search);
        }
        return result;
    }

    private KType Named(string fqName, bool nullable = false) => new() { ClassName = _names.Class(fqName), Nullable = nullable };
    private static KType Named(int className, bool nullable = false) => new() { ClassName = className, Nullable = nullable };
    private KType Platform(string fqName)
    {
        var lower = Named(fqName);
        lower.FlexibleTypeCapabilitiesId = _names.String("dotkt.clr.PlatformType");
        lower.FlexibleUpperBound = Named(fqName, nullable: true);
        return lower;
    }
    private KType Platform(int className)
    {
        var lower = Named(className);
        lower.FlexibleTypeCapabilitiesId = _names.String("dotkt.clr.PlatformType");
        lower.FlexibleUpperBound = Named(className, nullable: true);
        return lower;
    }
    private KType ExternalCompanion(CompanionCatalogEntry entry, bool platform)
    {
        var className = _names.Class(entry.SemanticPackage, entry.SemanticClasses);
        return platform ? Platform(className) : Named(className);
    }
    private KType FromDefinition(TypeDefinitionHandle handle, bool platform)
    {
        var def = _md.GetTypeDefinition(handle);
        var name = DefinitionKotlinName(_md, handle);
        var className = DefinitionKotlinClassName(_md, handle);
        if (_semanticTypeNames.TryGetValue(handle, out var semanticName))
            return platform ? Platform(semanticName) : Named(semanticName);
        if (_delegateDefinitions.ContainsKey(name) && !def.GetGenericParameters().Any())
            return DecodeDelegate(handle);
        return platform ? Platform(className) : Named(className);
    }
    private KType FromReference(TypeReferenceHandle handle, bool platform)
    {
        if (_companionCatalog.TryResolve(_md, handle, out var externalCompanion))
            return ExternalCompanion(externalCompanion, platform);
        var name = ReferenceKotlinName(_md, handle);
        var className = ReferenceKotlinClassName(_md, handle);
        var metadataFull = ReferenceMetadataName(_md, handle);
        // Semantic BCL projections intentionally replace the physical class path. Otherwise preserve the exact nested
        // NameTable path assembled above: an arity-disambiguated display name can differ from metadataFull without
        // being a semantic projection, and reparsing it would turn its last declaring-type segment into a package.
        if (_restoreKotlinCollections && KotlinCollection(name) is string collection)
            return platform ? Platform(collection) : Named(collection);
        if (_restoreKotlinCollections && metadataFull == "System.IComparable")
            return platform ? Platform("kotlin.Comparable") : Named("kotlin.Comparable");
        if (metadataFull == "System.String")
            return platform ? Platform("kotlin.String") : Named("kotlin.String");
        if (metadataFull == "System.Object")
            return platform ? Platform("kotlin.Any") : Named("kotlin.Any");
        return platform ? Platform(className) : Named(className);
    }
    private static KType Nullable(KType type)
    {
        var result = type.Clone();
        result.ClearFlexibleTypeCapabilitiesId();
        result.FlexibleUpperBound = null;
        result.Nullable = true;
        return result;
    }
    private KType Any(bool nullable) => Named("kotlin.Any", nullable);
    private KType Array(KType element) {
        // A Kotlin specialized array is an array of the NON-NULL primitive: `IntArray` is `int32[]`, while
        // `Array<Int?>` is a different Kotlin type with a different physical form (`object[]` — #86 D2). Collapsing a
        // nullable element onto the specialized name dropped the `?` and re-imported `Array<Int?>` as `IntArray`, so a
        // consumer holding the real thing could not pass it to the slot that declared it.
        if (element.HasClassName && !element.Nullable && _names.ClassName(element.ClassName) is string elementName &&
            PrimitiveArray(elementName) is string specialized)
            return Named(specialized);
        var t = Named("kotlin.Array");
        t.Argument.Add(new KType.Types.Argument { Projection = KType.Types.Argument.Types.Projection.Inv, Type = element });
        return t;
    }
    private static string FullName(string ns, string name) => string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    private static string StripArity(string name) => name.Contains('`') ? name[..name.IndexOf('`')] : name;

    private static string? KotlinCollection(string name) => name switch
    {
        "System.Collections.Generic.IEnumerable" => "kotlin.collections.Iterable",
        "System.Collections.Generic.IReadOnlyCollection" => "kotlin.collections.Collection",
        "System.Collections.Generic.ICollection" => "kotlin.collections.MutableCollection",
        "System.Collections.Generic.IReadOnlyList" => "kotlin.collections.List",
        "System.Collections.Generic.IList" => "kotlin.collections.MutableList",
        "System.Collections.Generic.IDictionary" => "kotlin.collections.Map",
        _ => null,
    };

    private static string? PrimitiveArray(string element) => element switch
    {
        "kotlin.Boolean" => "kotlin.BooleanArray",
        "kotlin.Char" => "kotlin.CharArray",
        "kotlin.Byte" => "kotlin.ByteArray",
        "kotlin.UByte" => "kotlin.UByteArray",
        "kotlin.Short" => "kotlin.ShortArray",
        "kotlin.UShort" => "kotlin.UShortArray",
        "kotlin.Int" => "kotlin.IntArray",
        "kotlin.UInt" => "kotlin.UIntArray",
        "kotlin.Long" => "kotlin.LongArray",
        "kotlin.ULong" => "kotlin.ULongArray",
        "kotlin.Float" => "kotlin.FloatArray",
        "kotlin.Double" => "kotlin.DoubleArray",
        _ => null,
    };

    private bool IsKnownDelegate(string name) =>
        name.StartsWith("System.Func", StringComparison.Ordinal) ||
        name.StartsWith("System.Action", StringComparison.Ordinal) ||
        name.StartsWith("System.EventHandler", StringComparison.Ordinal) ||
        _delegateDefinitions.ContainsKey(name);

    private KType ConstructDelegate(string name, ImmutableArray<KType> typeArguments)
    {
        if (name.StartsWith("System.Func", StringComparison.Ordinal))
        {
            if (typeArguments.Length == 0) return Any(nullable: true);
            return Function(typeArguments[..^1], typeArguments[^1]);
        }
        if (name.StartsWith("System.Action", StringComparison.Ordinal))
            return Function(typeArguments, Named("kotlin.Unit"));
        if (name.StartsWith("System.EventHandler", StringComparison.Ordinal))
        {
            var args = new List<KType> { Platform("kotlin.Any") };
            args.AddRange(typeArguments);
            return Function(args, Named("kotlin.Unit"));
        }
        if (!_delegateDefinitions.TryGetValue(name, out var handle)) return Any(nullable: true);
        return Substitute(DecodeDelegate(handle), typeArguments);
    }

    private KType KnownDelegate(string name, ImmutableArray<KType> typeArguments) =>
        ConstructDelegate(name, typeArguments);

    private KType DecodeDelegate(TypeDefinitionHandle handle)
    {
        var def = _md.GetTypeDefinition(handle);
        var invokeHandle = def.GetMethods()
            .FirstOrDefault(h => _md.GetString(_md.GetMethodDefinition(h).Name) == "Invoke");
        if (invokeHandle.IsNil) return Any(nullable: true);
        var ids = def.GetGenericParameters().ToDictionary(h => h, h => _md.GetGenericParameter(h).Index);
        var context = new GenericContext(handle, invokeHandle, ids);
        var sig = _md.GetMethodDefinition(invokeHandle).DecodeSignature(this, context);
        return Function(sig.ParameterTypes, sig.ReturnType);
    }

    private KType DecodeExternalDelegate(DelegateCatalogEntry entry)
    {
        var key = entry.DefinitionPath + "\0" +
            entry.TypeDefinitionRow.ToString(CultureInfo.InvariantCulture);
        if (_externalDelegateShapes.TryGetValue(key, out var cached))
            return cached.Clone();

        using var file = File.OpenRead(entry.DefinitionPath);
        using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata)
            throw new InvalidDataException(
                $"delegate catalog target is not a managed PE: {entry.DefinitionPath}");
        var md = pe.GetMetadataReader();
        var handle = MetadataTokens.TypeDefinitionHandle(entry.TypeDefinitionRow);
        if (handle.IsNil || MetadataTokens.GetRowNumber(handle) > md.TypeDefinitions.Count)
            throw new InvalidDataException(
                $"delegate catalog contains an invalid TypeDef row for {entry.MetadataName}");
        var decoder = new SignatureDecoder(
            md,
            _names,
            new MetadataAttributes(md),
            ArityNames.Create(
                md,
                Environment.GetEnvironmentVariable("DOTKT_DLL2KLIB_ARITY_CLASHES")),
            _delegateCatalog,
            _companionCatalog,
            _innerCatalog);
        var shape = decoder.DecodeDelegate(handle);
        _externalDelegateShapes[key] = shape;
        return shape.Clone();
    }

    private KType Function(IEnumerable<KType> parameters, KType returnType)
    {
        var ps = parameters.ToList();
        var result = Named($"kotlin.Function{ps.Count}");
        foreach (var item in ps.Append(returnType))
            result.Argument.Add(new KType.Types.Argument {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = item.Clone(),
            });
        return result;
    }

    private KType Substitute(KType source, ImmutableArray<KType> args)
    {
        if (source.HasTypeParameter && source.TypeParameter >= 0 && source.TypeParameter < args.Length)
            return args[source.TypeParameter].Clone();
        var copy = source.Clone();
        for (var i = 0; i < copy.Argument.Count; i++)
            if (copy.Argument[i].Type is { } arg)
                copy.Argument[i].Type = Substitute(arg, args);
        if (copy.FlexibleUpperBound is { } upper)
            copy.FlexibleUpperBound = Substitute(upper, args);
        return copy;
    }

    private bool IsSystemType(EntityHandle handle, string ns, string name)
    {
        if (handle.IsNil) return false;
        return handle.Kind switch {
            HandleKind.TypeReference => MatchReference(_md.GetTypeReference((TypeReferenceHandle)handle)),
            HandleKind.TypeDefinition => MatchDefinition(_md.GetTypeDefinition((TypeDefinitionHandle)handle)),
            _ => false,
        };
        bool MatchReference(TypeReference t) => _md.GetString(t.Namespace) == ns && _md.GetString(t.Name) == name;
        bool MatchDefinition(TypeDefinition t) => _md.GetString(t.Namespace) == ns && _md.GetString(t.Name) == name;
    }
}
