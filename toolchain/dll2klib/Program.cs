using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DotKt.Bir;
using DotKt.Klib.Metadata;
using Google.Protobuf;
using KType = DotKt.Klib.Metadata.Type;

internal sealed record Dll2KlibProjectionState(
    int Version,
    string[] ArityClashes,
    Dll2KlibProjectionInput[] Inputs);

internal sealed record Dll2KlibProjectionInput(
    string Path,
    string Mvid,
    string[] ArityKeys,
    string[] ReferencedArityKeys,
    string[] Dependencies);

internal static class Program
{
    private const int ProjectionStateVersion = 4;

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
                // A correct projection needs the complete resolved assembly universe. The old two-path form existed
                // only as the batch coordinator's child-process protocol; the in-process coordinator has no worker
                // mode and must not reconstruct that universe from one DLL.
                throw new InvalidOperationException(
                    "direct projection requires the complete resolved reference set; " +
                    "use 'dll2klib --out <directory> @<references.rsp>'");
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
        var discovery = DiscoverBatchCatalogs(resolvedInputs);
        var inputs = discovery.Inputs;
        var work = inputs.Select(input => (
            Input: input,
            Output: Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(input) + ".klib")))
            .ToArray();
        // Kotlin classifiers cannot overload on generic arity. Compute the
        // stable source-name rule once from the complete MSBuild-resolved
        // reference set, then give every otherwise-independent worker the
        // same tiny naming catalog (Task + Task`1 -> Task / Task1; a singleton
        // List`1 remains List).
        var projectedPaths = inputs.ToHashSet(StringComparer.Ordinal);
        var inputMetadata = discovery.InputMetadata;
        var arityClashes = inputMetadata
            .Where(input => projectedPaths.Contains(input.Path))
            .SelectMany(input => input.PublicArities)
            .GroupBy(member => member.Key, StringComparer.Ordinal)
            .Where(group => group.Select(member => member.Arity).Distinct().Skip(1).Any())
            .Select(group => group.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        var delegateCatalog = discovery.Delegates;
        var companionCatalog = discovery.Companions;
        var innerCatalog = discovery.Inners;
        using var publicTypeCatalog = discovery.PublicTypes;
        var collisions = work.GroupBy(x => x.Output, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Select(y => y.Input).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .ToArray();
        if (collisions.Length != 0)
            throw new InvalidOperationException(
                "different reference assemblies map to the same KLIB output: " +
                string.Join(", ", collisions.Select(x => Path.GetFileName(x.Key))));

        Directory.CreateDirectory(outputDirectory);
        var projectionCatalogPath = Path.Combine(outputDirectory, ".dll2klib-projection-catalog.json");
        // Every cross-assembly catalog fact originates in one resolved DLL. Persist only each DLL's identity and
        // direct TypeRef edges, then compare the old and new graphs so a changed/removed root invalidates exactly its
        // reverse dependents. Arity naming is the one whole-universe rule: when its collision set changes, the DLLs
        // that define the affected source name become additional roots.
        var projectionInputs = inputMetadata
            .Select(input => new Dll2KlibProjectionInput(
                input.Path,
                input.Mvid,
                input.PublicArities.Select(member => member.Key)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray(),
                input.ReferencedArityKeys,
                publicTypeCatalog.DirectDependenciesOf(input.Path)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray()))
            .OrderBy(input => input.Path, StringComparer.Ordinal)
            .ToArray();
        var projectionState = new Dll2KlibProjectionState(
            ProjectionStateVersion,
            arityClashes,
            projectionInputs);
        var projectionCatalog = JsonSerializer.Serialize(projectionState);
        var incompleteMarkerPath = Path.Combine(outputDirectory, ".dll2klib-incomplete");
        var previousState = File.Exists(incompleteMarkerPath)
            ? null
            : LoadProjectionState(projectionCatalogPath);
        var currentByPath = projectionInputs.ToDictionary(input => input.Path, StringComparer.Ordinal);
        var previousByPath = previousState?.Inputs.ToDictionary(input => input.Path, StringComparer.Ordinal)
            ?? new Dictionary<string, Dll2KlibProjectionInput>(StringComparer.Ordinal);
        var changedRoots = ChangedProjectionRoots(
            previousState,
            projectionState,
            previousByPath,
            currentByPath);
        var currentClosures = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var previousClosures = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var inputTimes = inputMetadata.ToDictionary(
            input => input.Path,
            input => input.LastWriteTimeUtc,
            StringComparer.Ordinal);
        var tool = Path.GetFullPath(typeof(Program).Assembly.Location);
        var toolTime = File.GetLastWriteTimeUtc(tool);
        var stale = work.Where(x =>
        {
            if (previousState is null) return true;
            if (!File.Exists(x.Output)) return true;
            var outputTime = File.GetLastWriteTimeUtc(x.Output);
            var currentDependencies = ClosureOf(x.Input, currentByPath, currentClosures);
            var previousDependencies = ClosureOf(x.Input, previousByPath, previousClosures);
            return outputTime < inputTimes[x.Input] ||
                outputTime < toolTime ||
                changedRoots.Contains(x.Input) ||
                currentDependencies.Any(changedRoots.Contains) ||
                previousDependencies.Any(changedRoots.Contains) ||
                currentDependencies.Any(path =>
                    inputTimes.TryGetValue(path, out var dependencyTime) && outputTime < dependencyTime);
        }).ToArray();
        if (stale.Length == 0)
        {
            if (!File.Exists(projectionCatalogPath) ||
                !StringComparer.Ordinal.Equals(File.ReadAllText(projectionCatalogPath), projectionCatalog))
                WriteAllTextAtomically(projectionCatalogPath, projectionCatalog);
            if (File.Exists(incompleteMarkerPath)) File.Delete(incompleteMarkerPath);
            Console.WriteLine($"dll2klib: {work.Length} KLIB(s) up to date");
            return 0;
        }

        var stageDirectory = Path.Combine(
            outputDirectory,
            $".dll2klib-stage-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageDirectory);
        try
        {
            var parallelism = jobs == 0 ? stale.Length : Math.Max(1, Math.Min(jobs, stale.Length));
            Console.WriteLine($"dll2klib: converting {stale.Length}/{work.Length} reference(s), jobs={parallelism}");
            var failures = new List<string>();
            var failureLock = new object();
            var inheritedArityClashes = string.Join(';', arityClashes);
            await Parallel.ForEachAsync(
                stale,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism },
                (item, _) =>
                {
                    try
                    {
                        Convert(
                            item.Input,
                            Path.Combine(stageDirectory, Path.GetFileName(item.Output)),
                            inheritedArityClashes,
                            delegateCatalog,
                            companionCatalog,
                            innerCatalog,
                            publicTypeCatalog);
                    }
                    catch (Exception ex)
                    {
                        lock (failureLock)
                            failures.Add($"{item.Input}: {ex.Message}");
                    }
                    return ValueTask.CompletedTask;
                });
            if (failures.Count != 0)
                throw new InvalidOperationException(
                    "assembly conversion failed: " + string.Join(", ", failures.Order(StringComparer.Ordinal)));

            // From this marker onward a crash may leave only part of the staged generation published. The next run
            // treats that state as a cold cache and repairs every output before trusting the graph again.
            WriteAllTextAtomically(incompleteMarkerPath, projectionCatalog);
            foreach (var item in stale)
                File.Move(
                    Path.Combine(stageDirectory, Path.GetFileName(item.Output)),
                    item.Output,
                    overwrite: true);
            WriteAllTextAtomically(projectionCatalogPath, projectionCatalog);
            File.Delete(incompleteMarkerPath);
            return 0;
        }
        finally
        {
            if (Directory.Exists(stageDirectory)) Directory.Delete(stageDirectory, recursive: true);
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
            "  dll2klib --out <directory> [--jobs <N>] @<references.rsp>\n" +
            "  --jobs 0 converts all stale references concurrently");
        return 2;
    }

    private static bool IsStandardLibrary(string input)
    {
        using var file = File.OpenRead(input);
        using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null) return false;
        return new MetadataAttributes(pe.GetMetadataReader()).IsStandardLibrary;
    }

    private static void Convert(
        string input,
        string output,
        string? inheritedArityClashes,
        DelegateReferenceCatalog delegateCatalog,
        CompanionReferenceCatalog companionCatalog,
        InnerReferenceCatalog innerCatalog,
        PublicTypeCatalog publicTypeCatalog)
    {
        using var file = File.OpenRead(input);
        // C# 14 extension grouping declarations are signature-only stubs whose non-callable ldnull/throw body is
        // part of the standard graph contract, so the scanner needs method bodies as well as metadata.
        using var pe = new PEReader(file, PEStreamOptions.PrefetchEntireImage);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null)
            throw new InvalidDataException($"not a managed PE: {input}");

        var md = pe.GetMetadataReader();
        var assemblyName = md.IsAssembly ? md.GetString(md.GetAssemblyDefinition().Name) : Path.GetFileNameWithoutExtension(input);
        var uniqueName = $"clr.{assemblyName}.{md.GetGuid(md.GetModuleDefinition().Mvid):N}";
        var arityNames = ArityNames.Create(md, inheritedArityClashes);
        using var scanner = new AssemblyScanner(
            input, pe, md, arityNames, inheritedArityClashes,
            delegateCatalog, companionCatalog, innerCatalog, publicTypeCatalog);
        var fragments = scanner.Scan();

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                Write(zip, "default/manifest", Manifest(uniqueName));
                // The manifest owns the ordinary unique name. KlibMetadataProtoBuf.Header carries Kotlin's Name
                // spelling instead, and the standard loader requires a special name (`<...>`) for a module.
                var header = new Header { ModuleName = $"<{uniqueName}>" };
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

    private static byte[] Manifest(string uniqueName) => System.Text.Encoding.UTF8.GetBytes(
        "abi_version=2.4.0\n" +
        "compiler_version=2.4.10\n" +
        "ir_signature_versions=1,2\n" +
        "metadata_version=2.4.0\n" +
        $"unique_name={uniqueName}\n");

    private sealed record InputMetadata(
        string Path,
        string Mvid,
        (string Key, int Arity)[] PublicArities,
        string[] ReferencedArityKeys,
        DateTime LastWriteTimeUtc);

    private sealed record BatchCatalogDiscovery(
        string[] Inputs,
        InputMetadata[] InputMetadata,
        DelegateReferenceCatalog Delegates,
        CompanionReferenceCatalog Companions,
        InnerReferenceCatalog Inners,
        PublicTypeCatalog PublicTypes);

    private static BatchCatalogDiscovery DiscoverBatchCatalogs(string[] resolvedInputs)
    {
        using var metadata = ReferenceMetadataSet.Open(resolvedInputs);
        // Response-file mode is the MSBuild/reference-set contract. The authoritative stdlib declaration surface is
        // already supplied as the frontend KLIB, so marked CLR stdlib twins produce no projected KLIB. They remain in
        // the metadata universe: referenced delegate TypeRefs are decoded from their actual TypeDefs exactly like
        // delegates in any other resolved assembly.
        var inputs = metadata.Assemblies
            .Where(input => !new MetadataAttributes(input.Reader).IsStandardLibrary)
            .Select(input => input.Path)
            .ToArray();
        var projectedPaths = inputs.ToHashSet(StringComparer.Ordinal);
        var inputMetadata = DiscoverInputMetadata(metadata.Assemblies, projectedPaths);
        var delegates = DelegateReferenceCatalog.Discover(metadata.Assemblies);
        var companions = CompanionReferenceCatalog.Discover(metadata.Assemblies);
        var inners = InnerReferenceCatalog.Discover(metadata.Assemblies);
        var publicTypes = PublicTypeCatalog.Discover(metadata.Assemblies);
        return new BatchCatalogDiscovery(
            inputs,
            inputMetadata,
            delegates,
            companions,
            inners,
            publicTypes);
    }

    private static InputMetadata[] DiscoverInputMetadata(
        IEnumerable<ReferenceMetadataSnapshot> inputs,
        IReadOnlySet<string> projectedPaths)
    {
        var result = new List<InputMetadata>();
        foreach (var input in inputs)
        {
            var path = input.Path;
            var md = input.Reader;
            var members = new List<(string Key, int Arity)>();
            string ScopeOf(TypeDefinitionHandle handle)
            {
                var def = md.GetTypeDefinition(handle);
                var parent = def.GetDeclaringType();
                var name = md.GetString(def.Name);
                if (!parent.IsNil) return ScopeOf(parent) + "." + name;
                var ns = md.GetString(def.Namespace);
                return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            }
            if (projectedPaths.Contains(path))
            {
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
                    members.Add((key, arity));
                }
            }
            result.Add(new InputMetadata(
                path,
                md.GetGuid(md.GetModuleDefinition().Mvid).ToString("N"),
                members.Distinct().OrderBy(member => member.Key, StringComparer.Ordinal)
                    .ThenBy(member => member.Arity).ToArray(),
                input.TypeReferences.Select(reference => reference.ArityKey)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray(),
                input.LastWriteTimeUtc));
        }
        return result.OrderBy(input => input.Path, StringComparer.Ordinal).ToArray();
    }

    private static Dll2KlibProjectionState? LoadProjectionState(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<Dll2KlibProjectionState>(File.ReadAllText(path));
            if (state is not { Version: ProjectionStateVersion, ArityClashes: not null, Inputs: not null } ||
                state.Inputs.Any(input => input is not
                    { Path: not null, Mvid: not null, ArityKeys: not null,
                      ReferencedArityKeys: not null, Dependencies: not null }) ||
                state.Inputs.Select(input => input.Path).Distinct(StringComparer.Ordinal).Count() != state.Inputs.Length)
                return null;
            return state;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HashSet<string> ChangedProjectionRoots(
        Dll2KlibProjectionState? previousState,
        Dll2KlibProjectionState currentState,
        IReadOnlyDictionary<string, Dll2KlibProjectionInput> previousByPath,
        IReadOnlyDictionary<string, Dll2KlibProjectionInput> currentByPath)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        if (previousState is null)
        {
            changed.UnionWith(currentByPath.Keys);
            return changed;
        }
        foreach (var input in currentByPath.Values)
            if (!previousByPath.TryGetValue(input.Path, out var previous) ||
                !StringComparer.Ordinal.Equals(previous.Mvid, input.Mvid) ||
                !previous.Dependencies.SequenceEqual(input.Dependencies, StringComparer.Ordinal))
                changed.Add(input.Path);
        foreach (var input in previousByPath.Values)
            if (!currentByPath.ContainsKey(input.Path))
                changed.Add(input.Path);

        var changedArityKeys = previousState.ArityClashes
            .Except(currentState.ArityClashes, StringComparer.Ordinal)
            .Concat(currentState.ArityClashes.Except(previousState.ArityClashes, StringComparer.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (changedArityKeys.Count != 0)
            foreach (var input in previousByPath.Values.Concat(currentByPath.Values))
                if (input.ArityKeys.Any(changedArityKeys.Contains) ||
                    input.ReferencedArityKeys.Any(changedArityKeys.Contains))
                    changed.Add(input.Path);
        return changed;
    }

    private static IReadOnlySet<string> ClosureOf(
        string input,
        IReadOnlyDictionary<string, Dll2KlibProjectionInput> graph,
        IDictionary<string, IReadOnlySet<string>> cache)
    {
        input = Path.GetFullPath(input);
        if (cache.TryGetValue(input, out var cached)) return cached;
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        if (graph.TryGetValue(input, out var root))
            foreach (var dependency in root.Dependencies) pending.Push(dependency);
        while (pending.TryPop(out var path))
        {
            if (!result.Add(path)) continue;
            if (graph.TryGetValue(path, out var entry))
                foreach (var dependency in entry.Dependencies) pending.Push(dependency);
        }
        result.Remove(input);
        cache[input] = result;
        return result;
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

    public static DelegateReferenceCatalog Discover(IEnumerable<ReferenceMetadataSnapshot> inputs)
    {
        var assemblies = inputs.ToArray();
        var definitions = new List<DelegateCatalogEntry>();
        foreach (var assembly in assemblies)
        {
            var path = assembly.Path;
            var md = assembly.Reader;
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
        foreach (var assembly in assemblies)
        {
            var path = assembly.Path;
            var md = assembly.Reader;
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
                aliases.Add(target with
                {
                    AssemblyName = forwardingAssembly,
                    MetadataName = metadataName,
                });
            }
        }
        return aliases.Count == 0
            ? result
            : new DelegateReferenceCatalog(definitions.Concat(aliases));
    }

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
        return scope.Kind switch
        {
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
        return implementation.Kind switch
        {
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

    public static InnerReferenceCatalog Discover(IEnumerable<ReferenceMetadataSnapshot> inputs)
    {
        var assemblies = inputs.ToArray();
        var entries = new List<InnerCatalogEntry>();
        foreach (var assembly in assemblies)
        {
            var path = assembly.Path;
            var md = assembly.Reader;
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
        foreach (var assembly in assemblies)
        {
            var md = assembly.Reader;
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

    public bool TryResolve(MetadataReader reader, TypeReferenceHandle handle, out InnerCatalogEntry entry)
    {
        var assemblyIdentity = ReferenceAssemblyIdentity(reader, handle);
        if (assemblyIdentity is not null && TryGet(assemblyIdentity, ReferenceName(reader, handle), out entry))
            return true;
        entry = null!;
        return false;
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
        return scope.Kind switch
        {
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
        var assembly = new AssemblyName(name)
        {
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
        return implementation.Kind switch
        {
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

    public static CompanionReferenceCatalog Discover(IEnumerable<ReferenceMetadataSnapshot> inputs)
    {
        var assemblies = inputs.ToArray();
        var definitions = new List<CompanionCatalogEntry>();
        foreach (var assembly in assemblies)
        {
            var path = assembly.Path;
            var md = assembly.Reader;
            var attrs = new MetadataAttributes(md);
            // A trusted companion carrier can occur only in a DotKt-produced assembly. Avoid indexing and probing
            // every TypeDef in ordinary framework and third-party assemblies merely to prove the carrier is absent.
            if (!attrs.IsDotKtAssembly) continue;
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
        foreach (var assembly in assemblies)
        {
            var md = assembly.Reader;
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
                aliases.Add(target with
                {
                    AssemblyIdentity = forwardingIdentity,
                    MetadataName = metadataName,
                });
            }
        }
        return aliases.Count == 0
            ? result
            : new CompanionReferenceCatalog(definitions.Concat(aliases));
    }

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
        var assembly = new AssemblyName(name)
        {
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

internal sealed class AssemblyScanner : IDisposable
{
    private sealed record CompanionCarrier(
        string Kind,
        string Owner,
        string Name,
        string Visibility,
        string PhysicalOwner,
        int PhysicalOwnerArity);

    private sealed record RichEnumEntryCarrier(string Name, string Field);

    private sealed record RichEnumCarrier(
        IReadOnlyList<RichEnumEntryCarrier> Entries,
        string Name,
        string Ordinal,
        string Values,
        string ValueOf);

    private sealed record BasicEnumCarrier(
        string Underlying,
        IReadOnlyList<(string Name, int Ordinal, string PhysicalValue)> Entries);

    private sealed record ValidatedRichEnumCarrier(
        IReadOnlyDictionary<FieldDefinitionHandle, string> EntryNames,
        IReadOnlySet<FieldDefinitionHandle> SyntheticFields,
        IReadOnlySet<MethodDefinitionHandle> SyntheticMethods);

    private sealed record ProjectedFunction(
        MethodDefinitionHandle Handle,
        Function Declaration,
        ImmutableArray<string> PhysicalParameters);

    private sealed record ProjectedConstructor(
        MethodDefinitionHandle Handle,
        Constructor Declaration,
        ImmutableArray<string> PhysicalParameters);

    private sealed record AttributeNamedArgument(string Kind, string Name, KType Type);

    private sealed record LocalInterfaceInstance(
        ResolvedTypeDefinition Definition,
        KType Type,
        PublicTypeSurface Surface,
        int Depth);

    private sealed record InheritedDefaultImplementation(
        MetadataReader Reader,
        MethodDefinitionHandle Declaration,
        string Name,
        ImmutableArray<KType> InterfaceArguments,
        SignatureDecoder Signatures,
        string? PropertyName,
        int AccessorKind,
        string? AssociationKey,
        string SlotKey,
        bool IsAbstract,
        int Depth,
        bool IsExplicit);

    private readonly MetadataReader _md;
    private readonly string _definitionPath;
    private readonly MetadataAttributes _attrs;
    private readonly ArityNames _arityNames;
    private readonly DelegateReferenceCatalog _delegateCatalog;
    private readonly CompanionReferenceCatalog _companionCatalog;
    private readonly InnerReferenceCatalog _innerCatalog;
    private readonly PublicTypeCatalog _publicTypeCatalog;
    private readonly Dictionary<TypeDefinitionHandle, CompanionCarrier> _companionCarriers = new();
    private readonly HashSet<TypeDefinitionHandle> _physicalCompanionCarriers = new();
    private readonly HashSet<TypeDefinitionHandle> _existentialCarriers = new();
    private readonly Dictionary<TypeDefinitionHandle, TypeDefinitionHandle> _genericStaticCarriers = new();
    private readonly Dictionary<TypeDefinitionHandle, TypeDefinitionHandle> _genericStaticCarrierByOwner = new();
    private readonly Dictionary<TypeDefinitionHandle, TypeDefinitionHandle> _liftedCompanions = new();
    private readonly Dictionary<TypeDefinitionHandle, CompanionCarrier> _companionsByOwner = new();
    private readonly HashSet<FieldDefinitionHandle> _singletonInstanceFields = new();
    private readonly Dictionary<TypeDefinitionHandle, string> _semanticOwnerNames = new();
    private readonly HashSet<TypeDefinitionHandle> _validatedCompanionOwners = new();
    private readonly Dictionary<string, TypeDefinitionHandle> _localDefinitions;
    private readonly CSharp14ExtensionCatalog _csharp14Extensions;
    private readonly SignatureDecoderSeeds _signatureSeeds;
    private readonly ExternalSignatureDecoderCache _externalSignatureDecoders;

    public AssemblyScanner(
        string definitionPath,
        PEReader pe,
        MetadataReader md,
        ArityNames arityNames,
        string? inheritedArityClashes,
        DelegateReferenceCatalog delegateCatalog,
        CompanionReferenceCatalog companionCatalog,
        InnerReferenceCatalog innerCatalog,
        PublicTypeCatalog publicTypeCatalog)
    {
        _definitionPath = Path.GetFullPath(definitionPath);
        _md = md;
        _attrs = new MetadataAttributes(md);
        _attrs.ValidateCarrierTargets(
            MetadataAttributes.DotKtNs + "KotlinCompanionExtensionAttribute",
            HandleKind.MethodDefinition,
            HandleKind.FieldDefinition);
        _attrs.ValidateCarrierTargets(
            MetadataAttributes.DotKtNs + "KotlinPropertyStorageAttribute",
            HandleKind.MethodDefinition);
        _attrs.ValidateCarrierTargets(
            MetadataAttributes.DotKtNs + "KotlinPropertyAccessorAttribute",
            HandleKind.MethodDefinition);
        _attrs.ValidateCarrierTargets(
            MetadataAttributes.DotKtNs + "KotlinSourceMethodAttribute",
            HandleKind.MethodDefinition);
        _attrs.ValidateCarrierTargets(
            MetadataAttributes.DotKtNs + "KotlinInnerConstructorFactoryAttribute",
            HandleKind.MethodDefinition);
        _attrs.ValidateCarrierTargets(
            MetadataAttributes.DotKtNs + "KotlinDeclarationIdentityAttribute",
            HandleKind.MethodDefinition);
        _attrs.ValidateCarrierTargets(
            MetadataAttributes.DotKtNs + "KotlinTypeParameterBoundsAttribute",
            HandleKind.MethodDefinition);
        _attrs.ValidateCarrierTargets(
            MetadataAttributes.DotKtNs + "KotlinSuspendResultAttribute",
            HandleKind.MethodDefinition);
        _attrs.ValidateCarrierTargets(
            MetadataAttributes.DotKtNs + "KotlinExtensionCoreAttribute",
            HandleKind.MethodDefinition);
        _attrs.ValidateCarrierTargets(
            MetadataAttributes.DotKtNs + "KotlinStaticCarrierAttribute",
            HandleKind.TypeDefinition);
        _attrs.ValidateCarrierTargets(
            MetadataAttributes.DotKtNs + "KotlinRichEnumAttribute",
            HandleKind.TypeDefinition);
        _attrs.ValidateCarrierTargets(
            MetadataAttributes.DotKtNs + "KotlinBasicEnumAttribute",
            HandleKind.TypeDefinition);
        _attrs.ValidateCarrierTargets(
            MetadataAttributes.DotKtNs + "KotlinExtensionReceiverAttribute",
            HandleKind.Parameter);
        ValidateKotlinExtensionReceiverCarriers();
        _arityNames = arityNames;
        _externalSignatureDecoders = new ExternalSignatureDecoderCache(inheritedArityClashes);
        _delegateCatalog = delegateCatalog;
        _companionCatalog = companionCatalog;
        _innerCatalog = innerCatalog;
        _publicTypeCatalog = publicTypeCatalog;
        _signatureSeeds = SignatureDecoderSeeds.Discover(md, arityNames);
        _csharp14Extensions = CSharp14ExtensionCatalog.Discover(pe, md, _attrs);
        var physicalTypes = md.TypeDefinitions
            .Select(handle => (
                Handle: handle,
                Name: MetadataTypeName(handle),
                Arity: md.GetTypeDefinition(handle).GetGenericParameters().Count))
            .GroupBy(x => (x.Name, x.Arity))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Handle).ToArray());
        var semanticTypeRows = md.TypeDefinitions
            .Select(handle => (
                Handle: handle,
                Name: KotlinFullName(handle),
                Arity: md.GetTypeDefinition(handle).GetGenericParameters().Count))
            .ToArray();
        // Awaitable/enumerable pattern discovery repeatedly resolves Kotlin-facing local names. Build that identity
        // once instead of reconstructing every TypeDef path for every projected class. Multiple definitions can share
        // one projected name (for example, a non-public generic-arity family); preserve the previous table-order first
        // match deliberately.
        _localDefinitions = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
        foreach (var row in semanticTypeRows)
            _localDefinitions.TryAdd(row.Name, row.Handle);
        var semanticTypes = semanticTypeRows
            .GroupBy(x => (x.Name, x.Arity))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Handle).ToArray());
        if (_attrs.IsDotKtAssembly)
        {
            foreach (var handle in md.TypeDefinitions)
            {
                if (!_attrs.Has(handle, "System.Runtime.CompilerServices.CompilerGeneratedAttribute", requireTrust: false)
                    || _attrs.CarrierType(handle, MetadataAttributes.DotKtNs + "KotlinTypeAttribute")
                        is not TypeNode.Fqn { Args: { Length: > 0 } args } semantic
                    || !args.All(arg => arg is TypeNode.Star))
                    continue;
                var carrier = md.GetTypeDefinition(handle);
                if ((carrier.Attributes & TypeAttributes.Interface) == 0
                    || carrier.GetGenericParameters().Count != 0)
                    throw new InvalidDataException(
                        $"Kotlin existential carrier '{MetadataTypeName(handle)}' must be a non-generic interface");
                if (!semanticTypes.TryGetValue((semantic.Name, args.Length), out var owners) || owners.Length != 1)
                    throw new InvalidDataException(
                        $"Kotlin existential owner '{semantic.Name}' arity {args.Length} resolved to "
                        + $"{(owners is null ? 0 : owners.Length)} physical types");
                _existentialCarriers.Add(handle);
            }
        }
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

        foreach (var handle in md.TypeDefinitions)
        {
            using var payload = _attrs.CarrierDocument(
                handle, MetadataAttributes.DotKtNs + "KotlinStaticCarrierAttribute");
            if (payload == null) continue;
            if (payload.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                payload.RootElement.EnumerateObject().Count() != 1 ||
                !payload.RootElement.TryGetProperty("owner", out var ownerElement) ||
                ownerElement.ValueKind != System.Text.Json.JsonValueKind.String ||
                ownerElement.GetString() is not string ownerName || string.IsNullOrEmpty(ownerName))
                throw new InvalidDataException(
                    "KotlinStaticCarrier payload must contain exactly one non-empty string 'owner'");
            var definition = md.GetTypeDefinition(handle);
            if ((definition.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public)
                throw new InvalidDataException(
                    $"KotlinStaticCarrier '{MetadataTypeName(handle)}' must be public");
            var ownerMatches = md.TypeDefinitions.Where(candidate =>
                KotlinFullName(candidate) == ownerName &&
                md.GetTypeDefinition(candidate).GetGenericParameters().Count > 0).ToArray();
            if (definition.GetGenericParameters().Count != 0 || ownerMatches.Length != 1 ||
                !_attrs.Has(handle, "System.Runtime.CompilerServices.CompilerGeneratedAttribute", requireTrust: false))
                throw new InvalidDataException(
                    $"malformed KotlinStaticCarrier '{MetadataTypeName(handle)}' for '{ownerName}'");
            ValidateGenericStaticCarrierMembers(handle);
            if (!_genericStaticCarriers.TryAdd(handle, ownerMatches[0]) ||
                !_genericStaticCarrierByOwner.TryAdd(ownerMatches[0], handle))
                throw new InvalidDataException($"multiple KotlinStaticCarrier declarations for '{ownerName}'");
        }

        // A trusted companion-extension carrier is compiler-owned metadata, so validate it over the complete
        // TypeDef/member universe before visibility filtering or ordinary class/file-facade projection. bir2cir
        // applies the same rule while indexing references: the carrier is valid only on a static member of a
        // Kotlin file facade, including private implementation members that dll2klib would otherwise skip.
        foreach (var handle in md.TypeDefinitions)
            ValidateCompanionExtensionPhysicalMembers(handle, md.GetTypeDefinition(handle));
    }

    public IReadOnlyList<Fragment> Scan()
    {
        var visible = _md.TypeDefinitions
            .Select(h => (Handle: h, Definition: _md.GetTypeDefinition(h)))
            .Where(x => IsPublicTopLevel(x.Definition))
            .Where(x => !_physicalCompanionCarriers.Contains(x.Handle))
            .Where(x => !_existentialCarriers.Contains(x.Handle))
            .Where(x => !_genericStaticCarriers.ContainsKey(x.Handle))
            .Where(x => _md.GetString(x.Definition.Name) != "<Module>")
            .GroupBy(x => _md.GetString(x.Definition.Namespace), StringComparer.Ordinal);

        var result = new List<Fragment>();
        foreach (var package in visible.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var names = new NameTable();
            var fragment = new PackageFragment
            {
                Package = new Package(),
                IsEmpty = false,
                FqName = package.Key,
            };
            fragment.Package.PackageFqName = names.Package(package.Key);
            var signatures = new SignatureDecoder(
                _md, names, _attrs, _arityNames, _delegateCatalog, _companionCatalog, _innerCatalog,
                _signatureSeeds,
                _externalSignatureDecoders,
                _definitionPath,
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
                MergeGenericStaticCarrier(handle, klass, names, signatures);
                fragment.Class.Add(klass);
                fragment.ClassName.Add(klass.FqName);
                var projectedName = semanticName ?? KotlinFullName(handle);
                if (!projectedBySemanticName.TryAdd(projectedName, klass))
                    throw new InvalidDataException(
                        $"multiple visible CLR types project Kotlin class '{projectedName}'");
                AddCompanion(handle, klass, fragment, names, signatures);
                ReadNestedClasses(handle, klass, fragment, names, signatures);
                ReadCSharpExtensions(handle, def, fragment.Package, names, signatures);
                ReadCSharp14StaticExtensions(handle, fragment.Package, names, signatures);
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
        // The KLIB reader probes root_package while resolving ordinary source,
        // even when this assembly has no root-package declarations. A packed
        // KLIB cannot represent an empty directory unless an entry is written,
        // so keep an explicit empty root fragment in every output.
        if (!result.Any(x => x.PackageName.Length == 0))
        {
            var names = new NameTable();
            var fragment = new PackageFragment
            {
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

    private RichEnumCarrier? ReadRichEnumCarrier(TypeDefinitionHandle handle)
    {
        using var doc = _attrs.CarrierDocument(
            handle, MetadataAttributes.DotKtNs + "KotlinRichEnumAttribute");
        if (doc is null) return null;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("malformed [KotlinRichEnum] carrier: expected an object");
        var properties = root.EnumerateObject().Select(property => property.Name).ToArray();
        if (properties.Length != 5 || properties.Distinct(StringComparer.Ordinal).Count() != 5 ||
            !properties.ToHashSet(StringComparer.Ordinal).SetEquals(["entries", "name", "ordinal", "values", "valueOf"]))
            throw new InvalidDataException(
                "malformed [KotlinRichEnum] carrier: expected exactly entries, name, ordinal, values, and valueOf");
        if (!root.TryGetProperty("name", out var nameFieldNode) ||
            nameFieldNode.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(nameFieldNode.GetString()) ||
            !root.TryGetProperty("ordinal", out var ordinalFieldNode) ||
            ordinalFieldNode.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(ordinalFieldNode.GetString()) ||
            !root.TryGetProperty("values", out var valuesNode) ||
            valuesNode.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(valuesNode.GetString()) ||
            !root.TryGetProperty("valueOf", out var valueOfNode) ||
            valueOfNode.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(valueOfNode.GetString()) ||
            !root.TryGetProperty("entries", out var entriesNode) ||
            entriesNode.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(
                "malformed [KotlinRichEnum] carrier: entries must be an array and physical member names must be strings");
        var entries = new List<RichEnumEntryCarrier>();
        var sourceNames = new HashSet<string>(StringComparer.Ordinal);
        var physicalNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entryNode in entriesNode.EnumerateArray())
        {
            if (entryNode.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(
                    "malformed [KotlinRichEnum] carrier: each entry must be an object");
            var entryProperties = entryNode.EnumerateObject().Select(property => property.Name).ToArray();
            if (entryProperties.Length != 2 || entryProperties.Distinct(StringComparer.Ordinal).Count() != 2 ||
                !entryProperties.ToHashSet(StringComparer.Ordinal).SetEquals(["name", "field"]) ||
                !entryNode.TryGetProperty("name", out var nameNode) || nameNode.ValueKind != JsonValueKind.String ||
                !entryNode.TryGetProperty("field", out var fieldNode) || fieldNode.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(nameNode.GetString()) || string.IsNullOrEmpty(fieldNode.GetString()))
                throw new InvalidDataException(
                    "malformed [KotlinRichEnum] carrier: each entry requires exactly non-empty name and field strings");
            var name = nameNode.GetString()!;
            var field = fieldNode.GetString()!;
            if (!sourceNames.Add(name) || !physicalNames.Add(field))
                throw new InvalidDataException(
                    "malformed [KotlinRichEnum] carrier: entry names and physical fields must be unique");
            entries.Add(new RichEnumEntryCarrier(name, field));
        }
        var nameField = nameFieldNode.GetString()!;
        var ordinalField = ordinalFieldNode.GetString()!;
        if (!physicalNames.Add(nameField) || !physicalNames.Add(ordinalField))
            throw new InvalidDataException(
                "malformed [KotlinRichEnum] carrier: physical fields must be distinct");
        return new RichEnumCarrier(
            entries, nameField, ordinalField, valuesNode.GetString()!, valueOfNode.GetString()!);
    }

    private BasicEnumCarrier? ReadBasicEnumCarrier(TypeDefinitionHandle handle)
    {
        using var doc = _attrs.CarrierDocument(
            handle, MetadataAttributes.DotKtNs + "KotlinBasicEnumAttribute");
        if (doc is null) return null;
        var root = doc.RootElement;
        var properties = root.ValueKind == JsonValueKind.Object
            ? root.EnumerateObject().Select(property => property.Name).ToArray()
            : [];
        if (properties.Length != 2 ||
            !properties.ToHashSet(StringComparer.Ordinal).SetEquals(["underlying", "entries"]) ||
            !root.TryGetProperty("underlying", out var underlyingNode) ||
            underlyingNode.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(underlyingNode.GetString()) ||
            !root.TryGetProperty("entries", out var entriesNode) || entriesNode.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(
                "malformed [KotlinBasicEnum] carrier: expected exactly underlying and entries");
        var entries = new List<(string Name, int Ordinal, string PhysicalValue)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entriesNode.EnumerateArray())
        {
            var entryProperties = entry.ValueKind == JsonValueKind.Object
                ? entry.EnumerateObject().Select(property => property.Name).ToArray()
                : [];
            if (entryProperties.Length != 3 ||
                !entryProperties.ToHashSet(StringComparer.Ordinal).SetEquals(["name", "ordinal", "physicalValue"]) ||
                !entry.TryGetProperty("name", out var nameNode) || nameNode.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(nameNode.GetString()) || !names.Add(nameNode.GetString()!) ||
                !entry.TryGetProperty("ordinal", out var ordinalNode) || !ordinalNode.TryGetInt32(out var ordinal) ||
                ordinal != entries.Count ||
                !entry.TryGetProperty("physicalValue", out var valueNode) || valueNode.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(valueNode.GetString()) || !values.Add(valueNode.GetString()!))
                throw new InvalidDataException(
                    "malformed [KotlinBasicEnum] carrier: entries require unique name/ordinal/value triples in declaration order");
            entries.Add((nameNode.GetString()!, ordinal, valueNode.GetString()!));
        }
        return new BasicEnumCarrier(underlyingNode.GetString()!, entries);
    }

    private void ValidateBasicEnumCarrier(
        TypeDefinitionHandle handle,
        TypeDefinition def,
        BasicEnumCarrier carrier)
    {
        var storage = carrier.Underlying switch
        {
            "System.SByte" => (ConstantTypeCode.SByte, (byte)0x04),
            "System.Byte" => (ConstantTypeCode.Byte, (byte)0x05),
            "System.Int16" => (ConstantTypeCode.Int16, (byte)0x06),
            "System.UInt16" => (ConstantTypeCode.UInt16, (byte)0x07),
            "System.Int32" => (ConstantTypeCode.Int32, (byte)0x08),
            "System.UInt32" => (ConstantTypeCode.UInt32, (byte)0x09),
            "System.Int64" => (ConstantTypeCode.Int64, (byte)0x0a),
            "System.UInt64" => (ConstantTypeCode.UInt64, (byte)0x0b),
            _ => throw new InvalidDataException(
                $"malformed [KotlinBasicEnum] carrier on '{MetadataTypeName(handle)}': illegal underlying type '{carrier.Underlying}'"),
        };
        var fields = def.GetFields().ToDictionary(
            fieldHandle => _md.GetString(_md.GetFieldDefinition(fieldHandle).Name),
            StringComparer.Ordinal);
        if (!fields.TryGetValue("value__", out var valueFieldHandle) ||
            !_md.GetBlobBytes(_md.GetFieldDefinition(valueFieldHandle).Signature)
                .SequenceEqual(new byte[] { 0x06, storage.Item2 }))
            throw new InvalidDataException(
                $"malformed [KotlinBasicEnum] carrier on '{MetadataTypeName(handle)}': value__ does not match '{carrier.Underlying}'");

        foreach (var entry in carrier.Entries)
        {
            if (!fields.TryGetValue(entry.Name, out var fieldHandle))
                throw new InvalidDataException(
                    $"malformed [KotlinBasicEnum] carrier on '{MetadataTypeName(handle)}': missing literal field '{entry.Name}'");
            var field = _md.GetFieldDefinition(fieldHandle);
            var constantHandle = field.GetDefaultValue();
            if ((field.Attributes & (FieldAttributes.Literal | FieldAttributes.Static)) !=
                    (FieldAttributes.Literal | FieldAttributes.Static) || constantHandle.IsNil)
                throw new InvalidDataException(
                    $"malformed [KotlinBasicEnum] carrier on '{MetadataTypeName(handle)}': '{entry.Name}' is not a literal field");
            var constant = _md.GetConstant(constantHandle);
            var blob = _md.GetBlobReader(constant.Value);
            var physical = constant.TypeCode switch
            {
                ConstantTypeCode.SByte => blob.ReadSByte().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.Byte => blob.ReadByte().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.Int16 => blob.ReadInt16().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.UInt16 => blob.ReadUInt16().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.Int32 => blob.ReadInt32().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.UInt32 => blob.ReadUInt32().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.Int64 => blob.ReadInt64().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.UInt64 => blob.ReadUInt64().ToString(CultureInfo.InvariantCulture),
                _ => null,
            };
            if (constant.TypeCode != storage.Item1 || physical != entry.PhysicalValue || blob.RemainingBytes != 0)
                throw new InvalidDataException(
                    $"malformed [KotlinBasicEnum] carrier on '{MetadataTypeName(handle)}': literal '{entry.Name}' " +
                    $"does not match {carrier.Underlying} value '{entry.PhysicalValue}'");
        }
    }

    private ValidatedRichEnumCarrier ValidateRichEnumCarrier(
        TypeDefinitionHandle handle,
        TypeDefinition def,
        RichEnumCarrier carrier,
        int projectedSelfName,
        NameTable names,
        SignatureDecoder signatures,
        GenericContext context)
    {
        if (def.GetGenericParameters().Count != 0 ||
            (def.Attributes & TypeAttributes.Interface) != 0 ||
            IsSystemType(def.BaseType, "System", "ValueType") ||
            IsSystemType(def.BaseType, "System", "Enum"))
            throw new InvalidDataException(
                $"malformed [KotlinRichEnum] carrier on '{MetadataTypeName(handle)}': expected a non-generic reference class");

        MethodDefinitionHandle RequireSyntheticMethod(string physicalName, bool isValues)
        {
            var matches = new List<MethodDefinitionHandle>();
            foreach (var methodHandle in def.GetMethods())
            {
                var method = _md.GetMethodDefinition(methodHandle);
                if (_md.GetString(method.Name) != physicalName ||
                    (method.Attributes & (MethodAttributes.Public | MethodAttributes.Static)) !=
                        (MethodAttributes.Public | MethodAttributes.Static) ||
                    !_attrs.Has(methodHandle, "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
                        requireTrust: false))
                    continue;
                var signature = method.DecodeSignature(signatures, context);
                var shapeMatches = signature.GenericParameterCount == 0 && (isValues
                    ? signature.ParameterTypes.Length == 0 &&
                        signatures.ArrayElement(signature.ReturnType) is { } element &&
                        IsSelfType(element, projectedSelfName)
                    : signature.ParameterTypes.Length == 1 &&
                        signature.ParameterTypes[0].HasClassName &&
                        names.ClassName(signature.ParameterTypes[0].ClassName) == "kotlin.String" &&
                        IsSelfType(signature.ReturnType, projectedSelfName));
                if (shapeMatches) matches.Add(methodHandle);
            }
            if (matches.Count != 1)
                throw new InvalidDataException(
                    $"malformed [KotlinRichEnum] carrier on '{MetadataTypeName(handle)}': " +
                    $"physical API '{physicalName}' has {matches.Count} matching declarations");
            return matches[0];
        }

        var entryNames = new Dictionary<FieldDefinitionHandle, string>();
        foreach (var entry in carrier.Entries)
        {
            var fields = def.GetFields().Where(fieldHandle =>
                _md.GetString(_md.GetFieldDefinition(fieldHandle).Name) == entry.Field).ToArray();
            if (fields.Length != 1)
                throw new InvalidDataException(
                    $"malformed [KotlinRichEnum] carrier on '{MetadataTypeName(handle)}': " +
                    $"physical entry field '{entry.Field}' has {fields.Length} declarations");
            var field = _md.GetFieldDefinition(fields[0]);
            var required = FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly;
            var fieldType = field.DecodeSignature(signatures, context);
            if ((field.Attributes & required) != required ||
                (field.Attributes & FieldAttributes.Literal) != 0 ||
                !IsSelfType(fieldType, projectedSelfName))
                throw new InvalidDataException(
                    $"malformed [KotlinRichEnum] carrier on '{MetadataTypeName(handle)}': " +
                    $"entry field '{entry.Field}' must be public static initonly and self-typed");
            entryNames.Add(fields[0], entry.Name);
        }
        FieldDefinitionHandle RequireMetadataField(string physicalName, string expectedType)
        {
            var fields = def.GetFields().Where(fieldHandle =>
                _md.GetString(_md.GetFieldDefinition(fieldHandle).Name) == physicalName).ToArray();
            if (fields.Length != 1)
                throw new InvalidDataException(
                    $"malformed [KotlinRichEnum] carrier on '{MetadataTypeName(handle)}': " +
                    $"metadata field '{physicalName}' has {fields.Length} declarations");
            var field = _md.GetFieldDefinition(fields[0]);
            var fieldType = field.DecodeSignature(signatures, context);
            var required = FieldAttributes.Public | FieldAttributes.InitOnly;
            if ((field.Attributes & required) != required ||
                (field.Attributes & (FieldAttributes.Static | FieldAttributes.Literal)) != 0 ||
                !fieldType.HasClassName || names.ClassName(fieldType.ClassName) != expectedType)
                throw new InvalidDataException(
                    $"malformed [KotlinRichEnum] carrier on '{MetadataTypeName(handle)}': " +
                    $"metadata field '{physicalName}' must be public instance initonly and {expectedType}-typed");
            return fields[0];
        }
        var syntheticFields = new HashSet<FieldDefinitionHandle>
        {
            RequireMetadataField(carrier.Name, "kotlin.String"),
            RequireMetadataField(carrier.Ordinal, "kotlin.Int"),
        };
        if (syntheticFields.Count != 2)
            throw new InvalidDataException(
                $"malformed [KotlinRichEnum] carrier on '{MetadataTypeName(handle)}': metadata fields must be distinct");
        var syntheticMethods = new HashSet<MethodDefinitionHandle>
        {
            RequireSyntheticMethod(carrier.Values, isValues: true),
            RequireSyntheticMethod(carrier.ValueOf, isValues: false),
        };
        if (syntheticMethods.Count != 2)
            throw new InvalidDataException(
                $"malformed [KotlinRichEnum] carrier on '{MetadataTypeName(handle)}': values and valueOf must be distinct");
        return new ValidatedRichEnumCarrier(entryNames, syntheticFields, syntheticMethods);
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
        var isFlagsEnum = isEnum && IsExactSystemFlagsEnum(handle, def);
        var richEnumCarrier = ReadRichEnumCarrier(handle);
        var basicEnumCarrier = ReadBasicEnumCarrier(handle);
        var isKotlinRichEnum = richEnumCarrier is not null;
        if (basicEnumCarrier is not null && !isEnum)
            throw new InvalidDataException(
                $"malformed [KotlinBasicEnum] carrier on '{MetadataTypeName(handle)}': target is not an enum");
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
        var kind = semanticKind ?? (isKotlinRichEnum ? 2 : isObject ? 5 : isInterface ? 1 : isEnum ? 2 : isAnnotation ? 4 : 0);
        var modality = isKotlinRichEnum ? 0
            : isKotlinSealed ? 3
            : kind == 1 || (def.Attributes & TypeAttributes.Abstract) != 0 ? 2
            : (def.Attributes & TypeAttributes.Sealed) == 0 ? 1 : 0;
        var result = new Class
        {
            FqName = semanticClassName ?? ClassName(handle, names),
            Flags = Flags.Declaration(
                modality,
                kind,
                isValue: isKotlinValue,
                isFun: _attrs.Has(handle, MetadataAttributes.DotKtNs + "KotlinFunInterfaceAttribute"),
                hasEnumEntries: isEnum || isKotlinRichEnum,
                isInner: isKotlinInner),
        };
        var clrVisibility = def.Attributes & TypeAttributes.VisibilityMask;
        if (clrVisibility is TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem)
            result.Flags = Flags.AsProtected(result.Flags);
        result.ClassAnnotation.Add(ClrExternalAnnotation(names, handle));
        result.Flags |= 1;

        var typeParameterIds = new Dictionary<GenericParameterHandle, int>();
        var retainedTypeParameters = new Dictionary<GenericParameterHandle, TypeParameter>();
        foreach (var gpHandle in def.GetGenericParameters())
        {
            var gp = _md.GetGenericParameter(gpHandle);
            var id = gp.Index;
            typeParameterIds[gpHandle] = id;
            if (id < capturedOuterTypeParameters.GetValueOrDefault()) continue;
            var parameter = new TypeParameter
            {
                Id = id,
                Name = names.String(_md.GetString(gp.Name)),
                Variance = (gp.Attributes & GenericParameterAttributes.VarianceMask) switch
                {
                    GenericParameterAttributes.Covariant => TypeParameter.Types.Variance.Out,
                    GenericParameterAttributes.Contravariant => TypeParameter.Types.Variance.In,
                    _ => TypeParameter.Types.Variance.Inv,
                },
            };
            result.TypeParameter.Add(parameter);
            retainedTypeParameters[gpHandle] = parameter;
        }

        var typeContext = new GenericContext(handle, default, typeParameterIds);
        var attributeNamedArguments = isAnnotation
            ? ProjectAttributeNamedArguments(handle, def, names, signatures, typeContext)
            : Array.Empty<AttributeNamedArgument>();
        var validatedRichEnum = richEnumCarrier is null ? null : ValidateRichEnumCarrier(
            handle, def, richEnumCarrier, result.FqName, names, signatures, typeContext);
        foreach (var (gpHandle, parameter) in retainedTypeParameters)
        {
            var gp = _md.GetGenericParameter(gpHandle);
            foreach (var constraint in KotlinNominalConstraints(_md, gp))
            {
                parameter.UpperBound.Add(
                    signatures.DecodeEntity(constraint.Type, typeContext, platform: false));
            }
        }
        if (isKotlinValue)
            AddValueClassRepresentation(handle, def, result, names, signatures, typeContext);
        var accessorPairs = KotlinAccessorPairs(handle, def, typeParameterIds);
        var customFieldAccessors = CustomFieldAccessors(def);
        var kotlinPropertyAccessorMethods = accessorPairs
            .SelectMany(x => new[] { x.Getter, x.Setter }.Where(handle => !handle.IsNil))
            .ToHashSet();
        var customPropertyAccessorMethods = customFieldAccessors.Values
            .SelectMany(x => x.Handles)
            .ToHashSet();
        var accessorMethods = kotlinPropertyAccessorMethods
            .Concat(def.GetProperties().SelectMany(propertyHandle =>
            {
                var accessors = _md.GetPropertyDefinition(propertyHandle).GetAccessors();
                return new[] { accessors.Getter, accessors.Setter }.Where(method => !method.IsNil);
            }))
            .Concat(customPropertyAccessorMethods)
            .ToHashSet();
        void AddProjectedInterfaces()
        {
            var implementedInterfaces = ProjectedPublicInterfaces(def, names, signatures, typeContext).ToList();
            var genericInterfaceNames = implementedInterfaces
                .Where(x => x.Argument.Count != 0 && x.HasClassName)
                .Select(x => names.ClassName(x.ClassName)?.Split('.').Last())
                .Where(x => x is not null)
                .ToHashSet(StringComparer.Ordinal);
            if (isInterface && def.GetGenericParameters().Any())
                genericInterfaceNames.Add(kotlinName);
            foreach (var supertype in implementedInterfaces)
            {
                // Drop the legacy non-generic shadow when the same CLR class implements a generic collection face.
                if (supertype.Argument.Count == 0 && supertype.HasClassName &&
                    names.ClassName(supertype.ClassName)?.Split('.').Last() is string simple &&
                    genericInterfaceNames.Contains(simple))
                    continue;
                if (signatures.IsKotlinComparable(supertype) && supertype.Argument.Count == 0)
                    continue;
                if (signatures.IsCompilerOwnedSlotCarrier(supertype))
                    continue;
                result.Supertype.Add(supertype);
            }
        }
        if (isEnum || isKotlinRichEnum)
        {
            var enumBase = new KType { ClassName = names.Class("kotlin.Enum") };
            var self = new KType { ClassName = result.FqName };
            foreach (var tp in result.TypeParameter)
                self.Argument.Add(new KType.Types.Argument
                {
                    Projection = KType.Types.Argument.Types.Projection.Inv,
                    Type = new KType { TypeParameter = tp.Id },
                });
            enumBase.Argument.Add(new KType.Types.Argument
            {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = self,
            });
            result.Supertype.Add(enumBase);
            // A rich enum is physically a reference class and can implement arbitrary Kotlin interfaces. Projecting
            // its enum identity must not discard those semantic supertypes or the carrier-restored erased ones.
            if (isKotlinRichEnum)
            {
                AddProjectedInterfaces();
                RestoreErasedSupertypes(
                    handle,
                    result,
                    signatures,
                    names,
                    capturedOuterTypeParameters.GetValueOrDefault());
            }
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
            AddProjectedInterfaces();
            if (result.Supertype.Count == 0)
                result.Supertype.Add(new KType { ClassName = names.Class("kotlin.Any") });
            RestoreErasedSupertypes(
                handle,
                result,
                signatures,
                names,
                capturedOuterTypeParameters.GetValueOrDefault());
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
            if (validatedRichEnum?.SyntheticMethods.Contains(methodHandle) == true) continue;
            // Compiler implementation methods (local functions, state-machine helpers, bridges) are executable CLR
            // details, not Kotlin declarations. Their MethodDefs stay in the assembly but never re-enter the source
            // API on round-trip.
            if (_attrs.IsDotKtAssembly && _attrs.Has(methodHandle,
                    "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
                    requireTrust: false))
                continue;
            if (!IsPublicOrProtected(method.Attributes)) continue;
            var declarationIdentity = KotlinDeclarationIdentityCarrier(methodHandle);
            var name = declarationIdentity?.Name ?? _md.GetString(method.Name);
            if (accessorMethods.Contains(methodHandle)) continue;
            var context = new GenericContext(handle, methodHandle, typeParameterIds);
            var sig = method.DecodeSignature(signatures, context);
            if (name == ".ctor")
            {
                if (isKotlinRichEnum) continue;
                var parameters = Parameters(methodHandle, method, sig.ParameterTypes, names, signatures, context)
                    .Skip(isKotlinInner ? 1 : 0);
                var constructor = new Constructor
                {
                    Flags = Flags.Visibility(method.Attributes),
                    ValueParameter = { parameters },
                };
                foreach (var named in attributeNamedArguments)
                {
                    var parameter = new ValueParameter
                    {
                        Name = names.String(named.Name),
                        Type = named.Type.Clone(),
                        // HAS_ANNOTATIONS + DECLARES_DEFAULT_VALUE. Absence at an application remains absence in IR;
                        // an explicit value is transported as a CLR named argument by the marker below.
                        Flags = (1 << 0) | (1 << 1),
                    };
                    parameter.ParameterAnnotation.Add(
                        ClrAttributeNamedArgumentAnnotation(names, named.Kind, named.Name));
                    constructor.ValueParameter.Add(parameter);
                }
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
                var suspendResult = (kotlinFlags & 4) != 0
                    ? _attrs.CarrierType(methodHandle,
                        MetadataAttributes.DotKtNs + "KotlinSuspendResultAttribute")
                        ?? throw new InvalidDataException(
                            $"suspend MethodDef '{_md.GetString(method.Name)}' has no trusted logical-result carrier")
                    : null;
                var sourceMethodName = KotlinSourceMethodName(methodHandle);
                var isComparableSlot = _attrs.IsDotKtAssembly &&
                    name == "CompareTo" &&
                    sig.ParameterTypes.Length == 1 &&
                    IsSelfType(sig.ParameterTypes[0], result.FqName);
                if (isComparableSlot) kotlinFlags |= 2;
                var function = new Function
                {
                    Name = names.String(sourceMethodName ?? (isComparableSlot ? "compareTo" : name)),
                    Flags = Flags.Callable(method.Attributes, modalityForMethod,
                        kotlinFlags,
                        isInline: _attrs.Has(methodHandle, MetadataAttributes.DotKtNs + "KotlinInlineAttribute")),
                    ReturnType = suspendResult is TypeNode logicalSuspendReturn
                        ? signatures.FromTypeNode(logicalSuspendReturn)
                        : declarationIdentity?.ReturnType is TypeNode semanticReturn
                        ? signatures.FromTypeNode(semanticReturn)
                        : ProjectReturn(methodHandle, method, sig.ReturnType, names, signatures, context),
                    ValueParameter = { Parameters(methodHandle, method, sig.ParameterTypes, names, signatures, context,
                        declarationIdentity?.Parameters,
                        declarationIdentity?.NullableWitnessTypeParameterIndices.Count ?? 0) },
                };
                PromoteContextParameters(method, function);
                // A C# extension MethodDef has two CLR meanings: it remains an ordinary callable static member of
                // its declaring class, while ReadCSharpExtensions separately publishes the namespace-scoped Kotlin
                // extension view. Do not turn this class member into a second extension declaration. DotKt member
                // extensions carry a compiler-owned receiver-role marker and are still restored here.
                PromoteReceiver(methodHandle, method, function, recognizeClrExtension: false);
                AddMethodTypeParameters(methodHandle, method, function, names, signatures, context,
                    declarationIdentity?.SemanticReifiedTypeParameterIndices);
                ApplyPInvokeProjection(method, function, names);
                // A member's declaring-class path already carries its physical owner. Preserve only the exact
                // frontend identity; ClrExternal is the top-level declaration transport.
                if (declarationIdentity is { } identity)
                    function.FunctionAnnotation.Add(
                        KotlinDeclarationIdentityAnnotation(names, identity.Id, ""));
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
            result.Function.Add(new Function
            {
                Name = names.String(operatorName),
                Flags = Flags.Callable(method.Attributes, modality: 0, kotlinFlags: 2) & ~(1 << 18),
                ReturnType = ProjectReturn(methodHandle, method, signature.ReturnType, names, signatures, context),
                ValueParameter = { parameters.Skip(1) },
            });
        }
        if (isFlagsEnum)
            AddFlagsEnumOperations(handle, result, names);

        // Suppress a public field only when a receiverless CLR Property row below projects that field's own Kotlin
        // declaration. An extension/context property may legally share the source name with an independent field-backed
        // property; accessorPairs contains precisely those receiver-bearing declarations, so folding its names into this
        // set would erase the receiverless property during DLL -> KLIB projection.
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var propertyHandle in def.GetProperties())
        {
            var property = _md.GetPropertyDefinition(propertyHandle);
            var accessors = property.GetAccessors();
            var getter = accessors.Getter.IsNil ? default(MethodDefinition?) : _md.GetMethodDefinition(accessors.Getter);
            var setter = accessors.Setter.IsNil ? default(MethodDefinition?) : _md.GetMethodDefinition(accessors.Setter);
            if (getter is not { } getMethod && setter is not { } setMethod) continue;
            // A DotKt member extension/context property is already projected below from this exact MethodSemantics
            // association. Treating its indexed CLR Property signature as a C# indexer as well invents operator
            // get/set declarations and can collide with a real Kotlin operator on the same class.
            var representativeHandle = accessors.Getter.IsNil ? accessors.Setter : accessors.Getter;
            if (kotlinPropertyAccessorMethods.Contains(representativeHandle) ||
                customPropertyAccessorMethods.Contains(representativeHandle))
                continue;
            var representative = getter ?? setter!.Value;
            var metadataPropertyName = _md.GetString(property.Name);
            var sourcePropertyName = KotlinPropertySourceName(property, accessors);
            var explicitInterfaceProperty = metadataPropertyName.Contains('.', StringComparison.Ordinal);
            // A private explicit-interface Property row describes the MethodImpl BODY, not the public Kotlin
            // declaration. Project it below from the authoritative interface Property/MethodSemantics row so NRT,
            // defaults, parameter names and generic metadata cannot drift with the private implementation signature.
            if (explicitInterfaceProperty || !IsPublicOrProtected(representative.Attributes)) continue;
            var context = new GenericContext(handle, accessors.Getter.IsNil ? accessors.Setter : accessors.Getter, typeParameterIds);
            var signature = property.DecodeSignature(signatures, context);
            var name = explicitInterfaceProperty
                ? metadataPropertyName[(metadataPropertyName.LastIndexOf('.') + 1)..]
                : sourcePropertyName;
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
            var projected = new Property
            {
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
        var explicitImplementations = new List<InheritedDefaultImplementation>();
        var interfaceKeys = ProjectedPublicInterfaces(
                def, names, signatures, typeContext, includePublicAncestors: true)
            .Select(TypeKey)
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
            var body = _md.GetMethodDefinition(bodyHandle);
            // A trusted Kotlin accessor carrier says this MethodImpl body is a physical implementation of an
            // already-declared Kotlin property, not another declaration to surface. Public/protected accessor bodies
            // are projected through KotlinAccessorPairs; private compiler bridges forward to that same declaration.
            // Reconstructing either bridge here would duplicate the property and, for member extension/context
            // properties, discard its receiver/context prefix by manufacturing a receiverless declaration.
            if (KotlinPropertyAccessorCarrier(bodyHandle) is not null) continue;
            if (TryResolvePublicInterfaceDeclaration(
                    _md,
                    implementation.MethodDeclaration,
                    signatures,
                    typeContext,
                    sourceArguments: [],
                    sourceArgumentSurface: default,
                    sourceDefinitionPath: null,
                    names,
                    signatures,
                    out var declarationReader,
                    out var declarationHandle,
                    out var declarationArguments,
                    out var declarationSignatures))
            {
                var declaration = declarationReader.GetMethodDefinition(declarationHandle);
                var declarationAttrs = declarationSignatures.Attributes;
                // A compiler-generated interface MethodDef is a physical slot (for example a suspend cold entry), not
                // another Kotlin declaration. Likewise, a private suspend MethodImpl body carrying the logical suspend
                // metadata is the exact-return bridge for an already-projected public override. The ordinary non-suspend
                // private MethodImpl path remains intact for interfaces whose hidden implementation must be surfaced as
                // a fake override (#451).
                if (declarationAttrs.IsDotKtAssembly && declarationAttrs.Has(
                        declarationHandle,
                        "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
                        requireTrust: false))
                    continue;
                var bodyKotlinFlags = _attrs.Int32(
                    bodyHandle, MetadataAttributes.DotKtNs + "KotlinFunctionAttribute") ?? 0;
                if (!IsPublicOrProtected(body.Attributes) && (bodyKotlinFlags & 4) != 0)
                    continue;
                var accessor = InheritedAccessor(declarationReader, declarationHandle);
                // Operators and other special-name slots do not become ordinary Kotlin functions. Property/indexer
                // and event accessors are carried by their authoritative MethodSemantics association instead.
                if (accessor is null && (declaration.Attributes & MethodAttributes.SpecialName) != 0)
                    continue;
                var associationKey = accessor is null
                    ? null
                    : AssemblySimpleName(declarationReader) + ":" +
                        accessor.Value.Association.Kind + ":" +
                        MetadataTokens.GetRowNumber(accessor.Value.Association) + ":" +
                        string.Join(",", declarationArguments.Select(TypeKey));
                var sourceName = accessor is null
                    ? KotlinSourceMethodName(bodyHandle) ??
                        SimpleMethodName(declarationReader.GetString(declaration.Name))
                    : SimpleMethodName(declarationReader.GetString(declaration.Name));
                explicitImplementations.Add(new InheritedDefaultImplementation(
                    declarationReader,
                    declarationHandle,
                    sourceName,
                    declarationArguments,
                    declarationSignatures,
                    accessor?.Name,
                    accessor?.Kind ?? 0,
                    associationKey,
                    AssemblySimpleName(declarationReader) + ":" +
                        MetadataTokens.GetRowNumber(declarationHandle) + ":" +
                        string.Join(",", declarationArguments.Select(TypeKey)),
                    IsAbstract: (body.Attributes & MethodAttributes.Abstract) != 0,
                    Depth: 0,
                    IsExplicit: !isInterface));
                continue;
            }
            var declarationName = implementation.MethodDeclaration.Kind switch
            {
                HandleKind.MemberReference => _md.GetString(
                    _md.GetMemberReference((MemberReferenceHandle)implementation.MethodDeclaration).Name),
                HandleKind.MethodDefinition => _md.GetString(
                    _md.GetMethodDefinition((MethodDefinitionHandle)implementation.MethodDeclaration).Name),
                _ => "",
            };
            declarationName = SimpleMethodName(declarationName);
            // The complete reference universe is required by worker mode. A direct public interface MethodImpl whose
            // declaration cannot be resolved has no trustworthy Kotlin signature to surface; do not reconstruct it
            // from the private body.
            throw new InvalidDataException(
                $"cannot resolve public interface declaration '{declarationName}' implemented by " +
                $"'{MetadataTypeName(handle)}' from the dll2klib reference catalog");
        }
        var functionKeys = result.Function.Select(f => FunctionKey(f, names))
            .ToHashSet(StringComparer.Ordinal);
        var inheritedDefaults = InheritedHiddenInterfaceDefaults(def, names, signatures, typeContext).ToArray();
        // A class-level MethodImpl is the authoritative implementation of its exact interface slot. If the same slot
        // is also reachable through an omitted non-public default provider, retain the class MethodImpl rather than
        // whichever hierarchy walk happened to be enumerated first.
        var surfacedImplementations = explicitImplementations.Concat(inheritedDefaults)
            .GroupBy(item => item.SlotKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var explicitEventShapes = new HashSet<string>(StringComparer.Ordinal);
        var inheritedAccessorGroups = surfacedImplementations
            .Where(item => item.AccessorKind is 1 or 2)
            .GroupBy(item => item.AssociationKey!, StringComparer.Ordinal);
        foreach (var accessorGroup in inheritedAccessorGroups)
        {
            var propertyName = accessorGroup.First().PropertyName!;
            var groupIsExplicit = accessorGroup.Any(item => item.IsExplicit);
            var pair = (
                Getter: accessorGroup.FirstOrDefault(item => item.AccessorKind == 1),
                Setter: accessorGroup.FirstOrDefault(item => item.AccessorKind == 2));
            var getterParameterCount = pair.Getter is null
                ? 0
                : pair.Getter.Reader.GetMethodDefinition(pair.Getter.Declaration)
                    .DecodeSignature(
                        pair.Getter.Signatures,
                        InheritedContext(
                            pair.Getter.Reader,
                            pair.Getter.Declaration,
                            pair.Getter.Reader.GetMethodDefinition(pair.Getter.Declaration)))
                    .ParameterTypes.Length;
            var setterParameterCount = pair.Setter is null
                ? 0
                : pair.Setter.Reader.GetMethodDefinition(pair.Setter.Declaration)
                    .DecodeSignature(
                        pair.Setter.Signatures,
                        InheritedContext(
                            pair.Setter.Reader,
                            pair.Setter.Declaration,
                            pair.Setter.Reader.GetMethodDefinition(pair.Setter.Declaration)))
                    .ParameterTypes.Length;
            if (getterParameterCount > 0 || setterParameterCount > 1)
            {
                if (pair.Getter is { } indexerGetter)
                {
                    var function = InheritedFunction(indexerGetter, "get", kotlinFlags: 2, names: names);
                    if (indexerGetter.IsExplicit)
                    {
                        var shape = FunctionShapeKey(function, names);
                        foreach (var declared in result.Function.Where(candidate =>
                            !Flags.IsStaticFunction(candidate.Flags) && FunctionShapeKey(candidate, names) == shape))
                            declared.Flags = Flags.AsOpen(declared.Flags);
                        result.Function.Add(function);
                    }
                    else if (functionKeys.Add(FunctionKey(function, names)))
                        result.Function.Add(function);
                }
                if (pair.Setter is { } indexerSetter)
                {
                    var function = InheritedFunction(indexerSetter, "set", kotlinFlags: 2, names: names);
                    if (indexerSetter.IsExplicit)
                    {
                        var shape = FunctionShapeKey(function, names);
                        foreach (var declared in result.Function.Where(candidate =>
                            !Flags.IsStaticFunction(candidate.Flags) && FunctionShapeKey(candidate, names) == shape))
                            declared.Flags = Flags.AsOpen(declared.Flags);
                        result.Function.Add(function);
                    }
                    else if (functionKeys.Add(FunctionKey(function, names)))
                        result.Function.Add(function);
                }
                continue;
            }
            KType propertyType;
            if (pair.Getter is { } getter)
            {
                var method = getter.Reader.GetMethodDefinition(getter.Declaration);
                var context = InheritedContext(getter.Reader, getter.Declaration, method);
                var signature = method.DecodeSignature(getter.Signatures, context);
                propertyType = ProjectInheritedReturn(
                    getter.Reader,
                    getter.Declaration,
                    method,
                    SubstituteTypeParameters(signature.ReturnType, getter.InterfaceArguments),
                    getter.Signatures);
            }
            else
            {
                var setter = pair.Setter!;
                var method = setter.Reader.GetMethodDefinition(setter.Declaration);
                var context = InheritedContext(setter.Reader, setter.Declaration, method);
                var signature = method.DecodeSignature(setter.Signatures, context);
                propertyType = ProjectInheritedType(
                    setter.Reader,
                    method.GetParameters().FirstOrDefault(handle =>
                        setter.Reader.GetParameter(handle).SequenceNumber == signature.ParameterTypes.Length),
                    SubstituteTypeParameters(signature.ParameterTypes[^1], setter.InterfaceArguments),
                    setter.Declaration,
                    setter.Signatures);
            }
            if (!groupIsExplicit && propertyNames.Contains(propertyName)) continue;
            if (groupIsExplicit)
            {
                // Kotlin has no explicit-interface-implementation declaration syntax. When a CLR class owns both a
                // final public member and a private MethodImpl for the same source signature, the ordinary member must
                // participate in frontend override resolution so `class D : C(), I` can name I's reimplementation.
                // This is only source-level openness: bir2cir reads the referenced MethodDef and keeps a non-virtual
                // CLR base member as a new slot rather than inventing a physical override.
                foreach (var declared in result.Property.Where(property =>
                    names.StringValue(property.Name) == propertyName &&
                    TypeKey(property.ReturnType) == TypeKey(propertyType) &&
                    (property.SetterValueParameter is not null) == (pair.Setter is not null)))
                    declared.Flags = Flags.AsOpen(declared.Flags);
            }
            var propertyIsAbstract = pair.Getter?.IsAbstract == true || pair.Setter?.IsAbstract == true;
            var propertyIsExplicit = pair.Getter?.IsExplicit == true || pair.Setter?.IsExplicit == true;
            var projectedProperty = new Property
            {
                Name = names.String(propertyName),
                ReturnType = propertyType,
                Flags = Flags.Property(
                    MethodAttributes.Public |
                        (propertyIsAbstract ? MethodAttributes.Abstract : 0) |
                        (propertyIsExplicit && !propertyIsAbstract ? MethodAttributes.Virtual : 0),
                    pair.Setter is not null,
                    isStatic: false,
                    memberKind: propertyIsExplicit ? Flags.FakeOverride : Flags.DeclarationMember),
                SetterValueParameter = pair.Setter is null
                    ? null
                    : new ValueParameter { Name = names.String("value"), Type = propertyType.Clone() },
            };
            if (propertyIsExplicit)
            {
                projectedProperty.PropertyAnnotation.Add(ExplicitSlotAnnotations(names));
                projectedProperty.Flags |= 1;
            }
            if (propertyIsAbstract)
            {
                if (pair.Getter is { } inheritedGetter)
                    projectedProperty.GetterFlags = Flags.Accessor(
                        MethodAttributes.Public | (inheritedGetter.IsAbstract ? MethodAttributes.Abstract : 0));
                if (pair.Setter is { } inheritedSetter)
                    projectedProperty.SetterFlags = Flags.Accessor(
                        MethodAttributes.Public | (inheritedSetter.IsAbstract ? MethodAttributes.Abstract : 0));
            }
            result.Property.Add(projectedProperty);
            propertyNames.Add(propertyName);
        }

        // A class can expose a public event and separately implement a same-named interface event explicitly. The
        // public Event row is the class receiver's source-visible member; the explicit slot remains reachable through
        // an interface cast. Do not manufacture a second same-named class property for that slot.
        var declaredEvents = def.GetEvents()
            .Select(eventHandle =>
            {
                var @event = _md.GetEventDefinition(eventHandle);
                var accessors = @event.GetAccessors();
                var accessorHandle = !accessors.Adder.IsNil ? accessors.Adder : accessors.Remover;
                return (Event: @event, AccessorHandle: accessorHandle);
            })
            .Where(item => !item.AccessorHandle.IsNil &&
                IsPublicOrProtected(_md.GetMethodDefinition(item.AccessorHandle).Attributes))
            .ToArray();
        var declaredEventNames = declaredEvents
            .Select(item => _md.GetString(item.Event.Name))
            .ToHashSet(StringComparer.Ordinal);
        var inheritedEvents = surfacedImplementations
            .Where(item => item.AccessorKind is 3 or 4)
            .GroupBy(item => item.AssociationKey!, StringComparer.Ordinal);
        foreach (var eventGroup in inheritedEvents)
        {
            var inherited = eventGroup.First();
            var eventName = inherited.PropertyName!;
            var groupIsExplicit = eventGroup.Any(item => item.IsExplicit);
            if (!groupIsExplicit && (propertyNames.Contains(eventName) || declaredEventNames.Contains(eventName)))
                continue;
            var reader = inherited.Reader;
            var declaration = reader.GetMethodDefinition(inherited.Declaration);
            var context = InheritedContext(reader, inherited.Declaration, declaration);
            var signature = declaration.DecodeSignature(inherited.Signatures, context);
            if (signature.ParameterTypes.Length != 1) continue;
            var parameterHandle = declaration.GetParameters().FirstOrDefault(handle =>
                reader.GetParameter(handle).SequenceNumber == 1);
            var physicalHandler = SubstituteTypeParameters(
                signature.ParameterTypes[0], inherited.InterfaceArguments);
            var handler = ProjectInheritedType(
                reader,
                parameterHandle,
                physicalHandler,
                inherited.Declaration,
                inherited.Signatures);
            var eventType = inherited.Signatures.NamedType("kotlin.clr.ClrEvent");
            eventType.Argument.Add(new KType.Types.Argument
            {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = handler,
            });
            var projectedEvent = new Property
            {
                Name = names.String(eventName),
                ReturnType = eventType,
                Flags = Flags.Property(
                    MethodAttributes.Public |
                        (eventGroup.Any(item => item.IsAbstract) ? MethodAttributes.Abstract : 0) |
                        (eventGroup.Any(item => item.IsExplicit && !item.IsAbstract)
                            ? MethodAttributes.Virtual
                            : 0),
                    canWrite: false,
                    isStatic: false,
                    memberKind: eventGroup.Any(item => item.IsExplicit)
                        ? Flags.FakeOverride
                        : Flags.DeclarationMember),
            };
            if (eventGroup.Any(item => item.IsExplicit))
            {
                explicitEventShapes.Add(eventName + ":" + TypeKey(physicalHandler));
                projectedEvent.PropertyAnnotation.Add(ExplicitSlotAnnotations(names));
                projectedEvent.Flags |= 1;
            }
            result.Property.Add(projectedEvent);
            propertyNames.Add(eventName);
        }

        foreach (var inherited in surfacedImplementations.Where(item => item.AccessorKind == 0))
        {
            var function = InheritedFunction(inherited, inherited.Name, kotlinFlags: 0, names: names);
            if (inherited.IsExplicit)
            {
                var shape = FunctionShapeKey(function, names);
                foreach (var declared in result.Function.Where(candidate =>
                    !Flags.IsStaticFunction(candidate.Flags) && FunctionShapeKey(candidate, names) == shape))
                    declared.Flags = Flags.AsOpen(declared.Flags);
                result.Function.Add(function);
            }
            else if (functionKeys.Add(FunctionKey(function, names)))
                result.Function.Add(function);
        }

        if (basicEnumCarrier is not null)
        {
            ValidateBasicEnumCarrier(handle, def, basicEnumCarrier);
            var literalNames = def.GetFields().Select(fieldHandle => _md.GetFieldDefinition(fieldHandle))
                .Where(field => (field.Attributes & (FieldAttributes.Literal | FieldAttributes.Static)) ==
                    (FieldAttributes.Literal | FieldAttributes.Static))
                .Select(field => _md.GetString(field.Name)).ToHashSet(StringComparer.Ordinal);
            if (!literalNames.SetEquals(basicEnumCarrier.Entries.Select(entry => entry.Name)))
                throw new InvalidDataException(
                    $"malformed [KotlinBasicEnum] carrier on '{MetadataTypeName(handle)}': entry map does not match literal fields");
            foreach (var entry in basicEnumCarrier.Entries)
                result.EnumEntry.Add(new EnumEntry { Name = names.String(entry.Name) });
        }

        foreach (var fieldHandle in def.GetFields())
        {
            // Suppress the exact ABI singleton slot validated from [KotlinCompanion], not a declaration selected by
            // source name. A companion may legally declare `val INSTANCE: Int`; that property must survive beside
            // the compiler-reserved self slot `$INSTANCE`.
            if (_singletonInstanceFields.Contains(fieldHandle)) continue;
            if (validatedRichEnum?.EntryNames.TryGetValue(fieldHandle, out var richEnumEntryName) == true)
            {
                result.EnumEntry.Add(new EnumEntry { Name = names.String(richEnumEntryName) });
                continue;
            }
            if (validatedRichEnum?.SyntheticFields.Contains(fieldHandle) == true) continue;
            var field = _md.GetFieldDefinition(fieldHandle);
            if (!IsPublicOrProtected(field.Attributes)) continue;
            var name = _md.GetString(field.Name);
            if (name.StartsWith('<') || propertyNames.Contains(name)) continue;
            if (isEnum && (field.Attributes & FieldAttributes.Literal) != 0 &&
                (field.Attributes & FieldAttributes.Static) != 0)
            {
                if (basicEnumCarrier is null)
                    result.EnumEntry.Add(new EnumEntry { Name = names.String(name) });
                continue;
            }
            var fieldType = ProjectType(fieldHandle, field.DecodeSignature(signatures, typeContext), handle, names, signatures, typeContext);
            var hasCustomAccessors = customFieldAccessors.TryGetValue(name, out var custom);
            var canWrite = hasCustomAccessors && (custom.Access & 2) != 0 ||
                (field.Attributes & (FieldAttributes.InitOnly | FieldAttributes.Literal)) == 0 &&
                !_attrs.Has(fieldHandle, MetadataAttributes.DotKtNs + "KotlinReadOnlyAttribute");
            var projected = new Property
            {
                Name = names.String(name),
                ReturnType = fieldType,
                Flags = Flags.Property(field.Attributes, canWrite),
                SetterValueParameter = canWrite
                    ? new ValueParameter { Name = names.String("value"), Type = fieldType.Clone() }
                    : null,
            };
            // An object/companion-object const is physically a CLR static literal (Literal cannot be instance), but
            // its Kotlin declaration is still an ordinary member of that singleton carrier. Do not turn it into a
            // Kotlin 2.4 companion-block static merely because of the required CLR storage bit. [KotlinObject] is the
            // exact producer-authored singleton fact; generic-static implementation carriers are a different set.
            if (_attrs.Has(handle, MetadataAttributes.DotKtNs + "KotlinObjectAttribute"))
                projected.Flags &= ~(1 << 19); // IS_STATIC
            if ((field.Attributes & FieldAttributes.Literal) != 0 &&
                CompileTimeValue(field, names) is { } constant)
            {
                projected.Flags |= (1 << 11) | (1 << 13); // IS_CONST + HAS_CONSTANT
                projected.CompileTimeValue = constant;
            }
            var isLateinit = _attrs.Has(fieldHandle, MetadataAttributes.DotKtNs + "KotlinLateinitAttribute");
            if (isLateinit)
                projected.Flags |= 1 << 12; // IS_LATEINIT
            if (hasCustomAccessors)
                ApplyAccessorFlags(projected, custom.Handles);
            else
            {
                projected.PropertyAnnotation.Add(ClrFieldAnnotation(names));
                if (isLateinit) projected.PropertyAnnotation.Add(ClrLateinitFieldAnnotation(names));
                projected.Flags |= 1;
            }
            result.Property.Add(projected);
        }

        foreach (var (ev, accessorHandle) in declaredEvents)
        {
            var accessor = _md.GetMethodDefinition(accessorHandle);
            var handler = signatures.DecodeEntity(ev.Type, typeContext, platform: false);
            var eventType = signatures.NamedType("kotlin.clr.ClrEvent");
            eventType.Argument.Add(new KType.Types.Argument
            {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = handler,
            });
            result.Property.Add(new Property
            {
                Name = names.String(_md.GetString(ev.Name)),
                ReturnType = eventType,
                Flags = Flags.Property(
                    explicitEventShapes.Contains(_md.GetString(ev.Name) + ":" + TypeKey(handler))
                        ? accessor.Attributes | MethodAttributes.Virtual
                        : accessor.Attributes,
                    canWrite: false,
                    (accessor.Attributes & MethodAttributes.Static) != 0),
            });
            propertyNames.Add(_md.GetString(ev.Name));
        }
        foreach (var pair in accessorPairs)
        {
            var representative = pair.Getter.IsNil ? pair.Setter : pair.Getter;
            result.Property.Add(KotlinAccessorProperty(handle, pair.Declaration, pair.Name,
                pair.Getter, pair.Setter, names, signatures, typeParameterIds,
                isStatic: (_md.GetMethodDefinition(representative).Attributes & MethodAttributes.Static) != 0));
        }
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

    private static string FunctionShapeKey(Function function, NameTable names) =>
        FunctionKey(function, names) + ":" + TypeKey(function.ReturnType) + "<" +
        string.Join(",", function.TypeParameter.Select(parameter =>
            Convert.ToBase64String(parameter.ToByteArray()))) + ">";

    private bool IsImplementedInterfaceDeclaration(
        EntityHandle declaration,
        HashSet<string> interfaceKeys,
        SignatureDecoder signatures,
        GenericContext context)
    {
        var owner = declaration.Kind switch
        {
            HandleKind.MemberReference => _md.GetMemberReference((MemberReferenceHandle)declaration).Parent,
            HandleKind.MethodDefinition => _md.GetMethodDefinition((MethodDefinitionHandle)declaration).GetDeclaringType(),
            _ => default,
        };
        if (owner.IsNil ||
            owner.Kind is not (HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification))
            return false;
        return interfaceKeys.Contains(TypeKey(signatures.DecodeEntity(owner, context, platform: false)));
    }

    // A metadata producer need not repeat the transitive InterfaceImpl closure on a class. If an inaccessible direct
    // edge is omitted from Kotlin, walk through it and retain every reachable public interface with its constructed
    // arguments substituted. Otherwise `C : hidden H`, `H : public I` loses the valid CLR `C <: I` relation merely
    // because C# happens to emit a redundant C -> I row while another producer does not.
    private IEnumerable<KType> ProjectedPublicInterfaces(
        TypeDefinition definition,
        NameTable names,
        SignatureDecoder signatures,
        GenericContext context,
        bool includePublicAncestors = false)
    {
        var projected = new Dictionary<string, KType>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var implementationHandle in definition.GetInterfaceImplementations())
        {
            var entity = _md.GetInterfaceImplementation(implementationHandle).Interface;
            if (IsCurrentExistentialCarrier(_md, entity)) continue;
            Visit(
                _md,
                entity,
                signatures.DecodeEntity(entity, context, platform: false),
                _publicTypeCatalog.Surface(_md, entity),
                definitionPath: null);
        }
        return projected.Values;

        void Visit(
            MetadataReader reader,
            EntityHandle entity,
            KType type,
            PublicTypeSurface surface,
            string? definitionPath)
        {
            if (!surface.IsInterface) return;
            var typeKey = TypeKey(type);
            if (surface.IsPublic)
            {
                projected.TryAdd(typeKey, type);
                // Compiler-owned slot carriers are deliberately absent from Kotlin's supertype graph together with
                // their physical inheritance. Ordinary public interfaces continue through the walk so metadata that
                // omits redundant public ancestor rows still contributes the complete accessible CLR relation and
                // MethodImpl declaration-key set.
                if (signatures.IsCompilerOwnedSlotCarrier(type)) return;
                // A public interface's own KLIB carries its public parent graph. Flattening that physical CLR closure
                // onto every implementer can invent extra Kotlin obligations (for example the non-generic
                // System.Collections.IEnumerable ancestor of Iterable<T>). Only MethodImpl declaration matching needs
                // to inspect beyond the first public edge.
                if (!includePublicAncestors) return;
            }
            if (!_publicTypeCatalog.TryResolveDefinition(
                    reader, entity, out var resolved, definitionPath)) return;
            var visitKey = AssemblySimpleName(resolved.Reader) + ":" +
                MetadataTokens.GetRowNumber(resolved.Handle) + ":" + typeKey;
            if (!visited.Add(visitKey)) return;

            var inheritedDefinition = resolved.Reader.GetTypeDefinition(resolved.Handle);
            var typeParameters = inheritedDefinition.GetGenericParameters()
                .ToDictionary(handle => handle, handle => resolved.Reader.GetGenericParameter(handle).Index);
            var inheritedContext = new GenericContext(resolved.Handle, default, typeParameters);
            var arguments = type.Argument
                .Where(argument => argument.Type is not null)
                .Select(argument => argument.Type!.Clone())
                .ToImmutableArray();
            var inheritedSignatures = DecoderFor(
                resolved.Reader, names, signatures, resolved.DefinitionPath);
            foreach (var parentHandle in inheritedDefinition.GetInterfaceImplementations())
            {
                var parent = resolved.Reader.GetInterfaceImplementation(parentHandle).Interface;
                if (IsCurrentExistentialCarrier(resolved.Reader, parent)) continue;
                var parentType = SubstituteTypeParameters(
                    inheritedSignatures.DecodeEntity(parent, inheritedContext, platform: false),
                    arguments);
                Visit(
                    resolved.Reader,
                    parent,
                    parentType,
                    _publicTypeCatalog.Surface(resolved.Reader, parent, surface.TypeArguments),
                    resolved.DefinitionPath);
            }
        }

        bool IsCurrentExistentialCarrier(MetadataReader reader, EntityHandle entity) =>
            IsCurrentAssembly(reader) && entity.Kind == HandleKind.TypeDefinition &&
            _existentialCarriers.Contains((TypeDefinitionHandle)entity);
    }

    // A CLR class can inherit the implementation of a public interface slot from a
    // non-public derived interface. Once that hidden edge is omitted from the Kotlin
    // surface, the public slot still needs a concrete declaration on the class;
    // otherwise every Kotlin subclass acquires a fictional abstract obligation.
    // C# emits the MethodImpl on J rather than C. Track the constructed interface
    // instances so J<T> : I<T> supplies the correctly substituted public I<T> slot.
    private IEnumerable<InheritedDefaultImplementation> InheritedHiddenInterfaceDefaults(
        TypeDefinition classDefinition,
        NameTable names,
        SignatureDecoder signatures,
        GenericContext classContext)
    {
        var instances = new List<LocalInterfaceInstance>();
        var visited = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var interfaceHandle in classDefinition.GetInterfaceImplementations())
        {
            var entity = _md.GetInterfaceImplementation(interfaceHandle).Interface;
            var surface = _publicTypeCatalog.Surface(_md, entity);
            if (surface.IsPublic && surface.IsInterface ||
                !_publicTypeCatalog.TryResolveDefinition(_md, entity, out var definition))
                continue;
            Visit(new LocalInterfaceInstance(
                definition,
                signatures.DecodeEntity(entity, classContext, platform: false),
                surface,
                0));
        }

        var candidates = new List<InheritedDefaultImplementation>();
        foreach (var instance in instances.Where(instance => !instance.Surface.IsPublic))
        {
            var reader = instance.Definition.Reader;
            var definition = reader.GetTypeDefinition(instance.Definition.Handle);
            var instanceSignatures = DecoderFor(
                reader, names, signatures, instance.Definition.DefinitionPath);
            var typeParameters = definition.GetGenericParameters()
                .ToDictionary(h => h, h => reader.GetGenericParameter(h).Index);
            var hiddenContext = new GenericContext(instance.Definition.Handle, default, typeParameters);
            var hiddenArguments = instance.Type.Argument
                .Where(argument => argument.Type is not null)
                .Select(argument => argument.Type!.Clone())
                .ToImmutableArray();
            foreach (var implementationHandle in definition.GetMethodImplementations())
            {
                var implementation = reader.GetMethodImplementation(implementationHandle);
                if (implementation.MethodBody.Kind != HandleKind.MethodDefinition)
                    continue;
                var bodyHandle = (MethodDefinitionHandle)implementation.MethodBody;
                if (!TryResolvePublicInterfaceDeclaration(
                        reader,
                        implementation.MethodDeclaration,
                        instanceSignatures,
                        hiddenContext,
                        hiddenArguments,
                        instance.Surface.TypeArguments,
                        instance.Definition.DefinitionPath,
                        names,
                        signatures,
                        out var declarationReader,
                        out var declarationHandle,
                        out var declarationArguments,
                        out var declarationSignatures))
                    continue;
                var declaration = declarationReader.GetMethodDefinition(declarationHandle);
                var body = reader.GetMethodDefinition(bodyHandle);
                if ((body.Attributes & MethodAttributes.Static) != 0 ||
                    (declaration.Attributes & MethodAttributes.Static) != 0)
                    continue;
                var accessor = InheritedAccessor(declarationReader, declarationHandle);
                // Operators and other CLR special-name members are not ordinary
                // inheritable Kotlin functions. Properties and events are handled
                // from their authoritative MethodSemantics association below.
                if (accessor is null && (declaration.Attributes & MethodAttributes.SpecialName) != 0)
                    continue;
                var slotKey = AssemblySimpleName(declarationReader) + ":" +
                    MetadataTokens.GetRowNumber(declarationHandle) + ":" +
                    string.Join(",", declarationArguments.Select(TypeKey));
                candidates.Add(new InheritedDefaultImplementation(
                    declarationReader,
                    declarationHandle,
                    SimpleMethodName(declarationReader.GetString(declaration.Name)),
                    declarationArguments,
                    declarationSignatures,
                    accessor?.Name,
                    accessor?.Kind ?? 0,
                    accessor is null
                        ? null
                        : AssemblySimpleName(declarationReader) + ":" +
                            accessor.Value.Association.Kind + ":" +
                            MetadataTokens.GetRowNumber(accessor.Value.Association) + ":" +
                            string.Join(",", declarationArguments.Select(TypeKey)),
                    slotKey,
                    (body.Attributes & MethodAttributes.Abstract) != 0,
                    instance.Depth,
                    IsExplicit: false));
            }
        }

        foreach (var group in candidates.GroupBy(candidate => candidate.SlotKey, StringComparer.Ordinal))
        {
            var depth = group.Min(candidate => candidate.Depth);
            var closest = group.Where(candidate => candidate.Depth == depth).ToArray();
            // An abstract reimplementation is a new abstract declaration for the same
            // CLR slot and suppresses every less-derived default body.
            if (closest.Any(candidate => candidate.IsAbstract)) continue;
            yield return closest[0];
        }

        void Visit(LocalInterfaceInstance instance)
        {
            var reader = instance.Definition.Reader;
            var handle = instance.Definition.Handle;
            var key = AssemblySimpleName(reader) + ":" + MetadataTokens.GetRowNumber(handle) + ":" +
                TypeKey(instance.Type);
            if (visited.TryGetValue(key, out var previousDepth) && previousDepth <= instance.Depth) return;
            visited[key] = instance.Depth;
            instances.Add(instance);
            var definition = reader.GetTypeDefinition(handle);
            var typeParameters = definition.GetGenericParameters()
                .ToDictionary(h => h, h => reader.GetGenericParameter(h).Index);
            var context = new GenericContext(handle, default, typeParameters);
            var arguments = instance.Type.Argument
                .Where(argument => argument.Type is not null)
                .Select(argument => argument.Type!.Clone())
                .ToImmutableArray();
            foreach (var parentHandle in definition.GetInterfaceImplementations())
            {
                var parentEntity = reader.GetInterfaceImplementation(parentHandle).Interface;
                if (!_publicTypeCatalog.TryResolveDefinition(
                        reader,
                        parentEntity,
                        out var parentDefinition,
                        instance.Definition.DefinitionPath)) continue;
                var parentType = DecoderFor(
                        reader, names, signatures, instance.Definition.DefinitionPath)
                    .DecodeEntity(parentEntity, context, platform: false);
                Visit(new LocalInterfaceInstance(
                    parentDefinition,
                    SubstituteTypeParameters(parentType, arguments),
                    _publicTypeCatalog.Surface(reader, parentEntity, instance.Surface.TypeArguments),
                    instance.Depth + 1));
            }
        }

    }

    private bool TryResolvePublicInterfaceDeclaration(
        MetadataReader reader,
        EntityHandle declarationEntity,
        SignatureDecoder sourceSignatures,
        GenericContext sourceContext,
        ImmutableArray<KType> sourceArguments,
        ImmutableArray<bool> sourceArgumentSurface,
        string? sourceDefinitionPath,
        NameTable names,
        SignatureDecoder currentSignatures,
        out MetadataReader declarationReader,
        out MethodDefinitionHandle declarationHandle,
        out ImmutableArray<KType> declarationArguments,
        out SignatureDecoder declarationSignatures)
    {
        declarationReader = null!;
        declarationHandle = default;
        declarationArguments = [];
        declarationSignatures = null!;

        EntityHandle owner;
        string methodName;
        MethodSignature<KType>? referenceSignature = null;
        if (declarationEntity.Kind == HandleKind.MethodDefinition)
        {
            var method = reader.GetMethodDefinition((MethodDefinitionHandle)declarationEntity);
            owner = method.GetDeclaringType();
            methodName = reader.GetString(method.Name);
        }
        else if (declarationEntity.Kind == HandleKind.MemberReference)
        {
            var member = reader.GetMemberReference((MemberReferenceHandle)declarationEntity);
            if (member.GetKind() != MemberReferenceKind.Method) return false;
            owner = member.Parent;
            methodName = reader.GetString(member.Name);
            referenceSignature = member.DecodeMethodSignature(sourceSignatures, sourceContext);
        }
        else
        {
            return false;
        }

        if (owner.IsNil ||
            owner.Kind is not (HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification) ||
            !_publicTypeCatalog.TryResolveDefinition(
                reader, owner, out var resolvedOwner, sourceDefinitionPath))
            return false;

        var ownerSurface = _publicTypeCatalog.Surface(reader, owner, sourceArgumentSurface);
        if (!ownerSurface.IsPublic || !ownerSurface.IsInterface) return false;

        var ownerType = SubstituteTypeParameters(
            sourceSignatures.DecodeEntity(owner, sourceContext, platform: false),
            sourceArguments);
        declarationArguments = ownerType.Argument
            .Where(argument => argument.Type is not null)
            .Select(argument => argument.Type!.Clone())
            .ToImmutableArray();
        var resolvedArguments = declarationArguments;
        declarationReader = resolvedOwner.Reader;
        declarationSignatures = DecoderFor(
            declarationReader, names, currentSignatures, resolvedOwner.DefinitionPath);

        if (declarationEntity.Kind == HandleKind.MethodDefinition &&
            reader.GetGuid(reader.GetModuleDefinition().Mvid) ==
                declarationReader.GetGuid(declarationReader.GetModuleDefinition().Mvid))
        {
            declarationHandle = (MethodDefinitionHandle)declarationEntity;
            return true;
        }

        var candidates = new List<MethodDefinitionHandle>();
        foreach (var candidateHandle in declarationReader.GetTypeDefinition(resolvedOwner.Handle).GetMethods())
        {
            var candidate = declarationReader.GetMethodDefinition(candidateHandle);
            if (!StringComparer.Ordinal.Equals(declarationReader.GetString(candidate.Name), methodName))
                continue;
            if (referenceSignature is not null &&
                candidate.GetGenericParameters().Count != referenceSignature.Value.GenericParameterCount)
                continue;
            var candidateContext = InheritedContext(declarationReader, candidateHandle, candidate);
            var candidateSignature = candidate.DecodeSignature(declarationSignatures, candidateContext);
            if (referenceSignature is not null)
            {
                // VAR positions in a MemberRef signature belong to its parent
                // interface, not to the type containing the MethodImpl.
                var expectedReturn = SubstituteTypeParameters(referenceSignature.Value.ReturnType, resolvedArguments);
                var expectedParameters = referenceSignature.Value.ParameterTypes
                    .Select(type => SubstituteTypeParameters(type, resolvedArguments)).ToArray();
                var actualReturn = SubstituteTypeParameters(candidateSignature.ReturnType, resolvedArguments);
                var actualParameters = candidateSignature.ParameterTypes
                    .Select(type => SubstituteTypeParameters(type, resolvedArguments)).ToArray();
                if (!actualReturn.Equals(expectedReturn) ||
                    !actualParameters.SequenceEqual(expectedParameters))
                    continue;
            }
            candidates.Add(candidateHandle);
        }
        if (candidates.Count != 1) return false;
        declarationHandle = candidates[0];
        return true;
    }

    private SignatureDecoder DecoderFor(
        MetadataReader reader,
        NameTable names,
        SignatureDecoder currentSignatures,
        string? definitionPath)
    {
        if (IsCurrentAssembly(reader)) return currentSignatures;
        if (definitionPath is null)
            throw new InvalidOperationException(
                "external signature decoding requires the resolved definition path");
        var source = _externalSignatureDecoders.Get(definitionPath, reader);
        return new SignatureDecoder(
            source.Reader,
            names,
            source.Attributes,
            source.ArityNames,
            _delegateCatalog,
            _companionCatalog,
            _innerCatalog,
            source.Seeds,
            _externalSignatureDecoders,
            definitionPath);
    }

    public void Dispose() => _externalSignatureDecoders.Dispose();

    private static GenericContext InheritedContext(
        MetadataReader reader,
        MethodDefinitionHandle methodHandle,
        MethodDefinition method)
    {
        var owner = method.GetDeclaringType();
        return new GenericContext(
            owner,
            methodHandle,
            reader.GetTypeDefinition(owner).GetGenericParameters()
                .ToDictionary(handle => handle, handle => reader.GetGenericParameter(handle).Index));
    }

    private static (string Name, int Kind, EntityHandle Association)? InheritedAccessor(
        MetadataReader reader,
        MethodDefinitionHandle methodHandle)
    {
        var owner = reader.GetMethodDefinition(methodHandle).GetDeclaringType();
        foreach (var propertyHandle in reader.GetTypeDefinition(owner).GetProperties())
        {
            var property = reader.GetPropertyDefinition(propertyHandle);
            var accessors = property.GetAccessors();
            if (accessors.Getter == methodHandle)
                return (SimpleMethodName(reader.GetString(property.Name)), 1, propertyHandle);
            if (accessors.Setter == methodHandle)
                return (SimpleMethodName(reader.GetString(property.Name)), 2, propertyHandle);
        }
        foreach (var eventHandle in reader.GetTypeDefinition(owner).GetEvents())
        {
            var @event = reader.GetEventDefinition(eventHandle);
            var accessors = @event.GetAccessors();
            if (accessors.Adder == methodHandle)
                return (SimpleMethodName(reader.GetString(@event.Name)), 3, eventHandle);
            if (accessors.Remover == methodHandle)
                return (SimpleMethodName(reader.GetString(@event.Name)), 4, eventHandle);
        }
        return null;
    }

    private KType ProjectInheritedReturn(
        MetadataReader reader,
        MethodDefinitionHandle methodHandle,
        MethodDefinition method,
        KType physical,
        SignatureDecoder signatures)
    {
        var returnHandle = method.GetParameters()
            .FirstOrDefault(handle => reader.GetParameter(handle).SequenceNumber == 0);
        if (signatures.ByRefElement(physical) is { } element) physical = element;
        return ProjectInheritedType(
            reader,
            returnHandle,
            physical,
            methodHandle,
            signatures,
            flowContract: true);
    }

    private IEnumerable<ValueParameter> InheritedParameters(
        MetadataReader reader,
        MethodDefinitionHandle methodHandle,
        MethodDefinition method,
        ImmutableArray<KType> types,
        NameTable names,
        SignatureDecoder signatures)
    {
        var rows = method.GetParameters()
            .Select(handle => (Handle: handle, Row: reader.GetParameter(handle)))
            .Where(parameter => parameter.Row.SequenceNumber > 0)
            .ToDictionary(parameter => parameter.Row.SequenceNumber);
        for (var i = 0; i < types.Length; i++)
        {
            if (!rows.TryGetValue(i + 1, out var entry))
            {
                yield return new ValueParameter
                {
                    Name = names.String($"arg{i}"),
                    Type = ProjectInheritedType(
                        reader, default, types[i], methodHandle, signatures),
                };
                continue;
            }
            var attrs = signatures.Attributes;
            var name = entry.Row.Name.IsNil ? $"arg{i}" : reader.GetString(entry.Row.Name);
            var type = ProjectInheritedType(
                reader, entry.Handle, types[i], methodHandle, signatures);
            var value = new ValueParameter
            {
                Name = names.String(string.IsNullOrEmpty(name) ? $"arg{i}" : name),
                Type = type,
                Flags = (entry.Row.Attributes & (ParameterAttributes.Optional | ParameterAttributes.HasDefault)) != 0 ||
                    attrs.Has(entry.Handle, "kotlin.clr.KotlinDefault", requireTrust: false)
                        ? 1 << 1
                        : 0,
            };
            if (attrs.Has(entry.Handle, "System.ParamArrayAttribute", requireTrust: false) &&
                signatures.ArrayElement(type) is { } element)
                value.VarargElementType = element;
            yield return value;
        }
    }

    private Function InheritedFunction(
        InheritedDefaultImplementation inherited,
        string name,
        int kotlinFlags,
        NameTable names)
    {
        var reader = inherited.Reader;
        var declarationHandle = inherited.Declaration;
        var declaration = reader.GetMethodDefinition(declarationHandle);
        var context = InheritedContext(reader, declarationHandle, declaration);
        var signature = declaration.DecodeSignature(inherited.Signatures, context);
        var returnType = SubstituteTypeParameters(signature.ReturnType, inherited.InterfaceArguments);
        var attrs = inherited.Signatures.Attributes;
        kotlinFlags |= attrs.Int32(
            declarationHandle, MetadataAttributes.DotKtNs + "KotlinFunctionAttribute") ?? 0;
        var logicalSuspendReturn = (kotlinFlags & 4) != 0
            ? attrs.CarrierType(
                declarationHandle, MetadataAttributes.DotKtNs + "KotlinSuspendResultAttribute")
                ?? throw new InvalidDataException(
                    $"suspend MethodDef '{reader.GetString(declaration.Name)}' has no trusted logical-result carrier")
            : null;
        var parameterTypes = signature.ParameterTypes
            .Select(type => SubstituteTypeParameters(type, inherited.InterfaceArguments))
            .ToImmutableArray();
        var function = new Function
        {
            Name = names.String(name),
            Flags = Flags.Callable(
                MethodAttributes.Public,
                modality: inherited.IsAbstract ? 2 : inherited.IsExplicit ? 1 : 0,
                kotlinFlags,
                memberKind: inherited.IsExplicit ? Flags.FakeOverride : Flags.DeclarationMember),
            ReturnType = logicalSuspendReturn is TypeNode suspendReturn
                ? SubstituteTypeParameters(
                    inherited.Signatures.FromTypeNode(suspendReturn), inherited.InterfaceArguments)
                : ProjectInheritedReturn(
                    reader, declarationHandle, declaration, returnType, inherited.Signatures),
            ValueParameter = {
                InheritedParameters(
                    reader,
                    declarationHandle,
                    declaration,
                    parameterTypes,
                    names,
                    inherited.Signatures)
            },
        };
        AddInheritedMethodTypeParameters(
            reader,
            declarationHandle,
            declaration,
            function,
            names,
            inherited.Signatures,
            context,
            inherited.InterfaceArguments);
        if (inherited.IsExplicit)
        {
            function.FunctionAnnotation.Add(ExplicitSlotAnnotations(names));
            function.Flags |= 1;
        }
        return function;
    }

    private KType ProjectInheritedType(
        MetadataReader reader,
        EntityHandle slot,
        KType physical,
        EntityHandle contextOwner,
        SignatureDecoder signatures,
        bool flowContract = false)
    {
        var attrs = signatures.Attributes;
        TypeNode? exact = null;
        string? carrierName = null;
        foreach (var carrier in new[] {
            "KotlinTypeAttribute",
            "KotlinSuspendFunctionTypeAttribute",
            "KotlinNullableGenericAttribute",
            "KotlinCollectionIdentityAttribute",
        })
        {
            exact = attrs.CarrierType(slot, MetadataAttributes.DotKtNs + carrier);
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
        if (attrs.Has(slot, MetadataAttributes.DotKtNs + "KotlinExtensionFunctionTypeAttribute"))
            result = signatures.AsExtensionFunction(result);
        if (attrs.Int32(slot, MetadataAttributes.DotKtNs + "KotlinContextFunctionTypeAttribute") is int contextCount)
            result = signatures.AsContextFunction(result, contextCount);
        var bytes = attrs.Nullability(slot);
        var contextByte = NullableContext(reader, attrs, contextOwner);
        if (attrs.IsDotKtAssembly && contextByte == 0) contextByte = 1;
        result = carrierName switch
        {
            "KotlinTypeAttribute" => result,
            "KotlinSuspendFunctionTypeAttribute" or "KotlinNullableGenericAttribute"
                => signatures.ApplyOuterNullability(result, bytes, contextByte),
            _ => signatures.ApplyNullability(result, bytes, contextByte),
        };
        if (flowContract && attrs.Has(slot, MetadataAttributes.MaybeNull, requireTrust: false))
            result = signatures.AsPlatform(result);
        else if (flowContract && attrs.Has(slot, MetadataAttributes.NotNull, requireTrust: false))
            result = signatures.AsNonNull(result);
        return result;
    }

    private void AddInheritedMethodTypeParameters(
        MetadataReader reader,
        MethodDefinitionHandle methodHandle,
        MethodDefinition method,
        Function function,
        NameTable names,
        SignatureDecoder signatures,
        GenericContext context,
        ImmutableArray<KType> interfaceArguments)
    {
        foreach (var parameterHandle in method.GetGenericParameters())
        {
            var parameter = reader.GetGenericParameter(parameterHandle);
            var projected = new TypeParameter
            {
                Id = 10000 + parameter.Index,
                Name = names.String(reader.GetString(parameter.Name)),
                Variance = TypeParameter.Types.Variance.Inv,
            };
            foreach (var constraint in KotlinNominalConstraints(reader, parameter))
            {
                projected.UpperBound.Add(SubstituteTypeParameters(
                    signatures.DecodeEntity(constraint.Type, context, platform: false),
                    interfaceArguments));
            }
            function.TypeParameter.Add(projected);
        }
        RestoreErasedMethodBounds(
            methodHandle,
            function.TypeParameter,
            signatures,
            interfaceArguments,
            signatures.Attributes);
    }

    private bool IsCurrentAssembly(MetadataReader reader) =>
        reader.GetGuid(reader.GetModuleDefinition().Mvid) == _md.GetGuid(_md.GetModuleDefinition().Mvid);

    private static string AssemblySimpleName(MetadataReader reader) => reader.IsAssembly
        ? reader.GetString(reader.GetAssemblyDefinition().Name)
        : reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString("N");

    private static byte NullableContext(
        MetadataReader reader,
        MetadataAttributes attrs,
        EntityHandle owner)
    {
        var current = owner;
        while (!current.IsNil)
        {
            if (attrs.Byte(current, MetadataAttributes.NullableContext, requireTrust: false) is byte value)
                return value;
            current = current.Kind switch
            {
                HandleKind.MethodDefinition => reader.GetMethodDefinition(
                    (MethodDefinitionHandle)current).GetDeclaringType(),
                HandleKind.TypeDefinition => reader.GetTypeDefinition(
                    (TypeDefinitionHandle)current).GetDeclaringType(),
                _ => default,
            };
        }
        return 0;
    }

    private static KType SubstituteTypeParameters(KType source, ImmutableArray<KType> arguments)
    {
        if (source.HasTypeParameter && source.TypeParameter >= 0 && source.TypeParameter < arguments.Length)
        {
            var replacement = arguments[source.TypeParameter].Clone();
            // Substitution replaces the classifier, not the use-site nullability. `T?` instantiated with a non-null
            // owner argument remains nullable; returning the argument verbatim weakened restored method bounds such
            // as `E : T?` to `E : String` on inherited interface declarations.
            if (source.Nullable) replacement.Nullable = true;
            return replacement;
        }
        var copy = source.Clone();
        for (var i = 0; i < copy.Argument.Count; i++)
            if (copy.Argument[i].Type is { } argument)
                copy.Argument[i].Type = SubstituteTypeParameters(argument, arguments);
        if (copy.FlexibleUpperBound is { } upper)
            copy.FlexibleUpperBound = SubstituteTypeParameters(upper, arguments);
        return copy;
    }


    private void AddMethodTypeParameters(
        MethodDefinitionHandle methodHandle,
        MethodDefinition method,
        Function function,
        NameTable names,
        SignatureDecoder signatures,
        GenericContext context,
        IReadOnlySet<int>? semanticReified)
    {
        foreach (var gpHandle in method.GetGenericParameters())
        {
            var gp = _md.GetGenericParameter(gpHandle);
            var parameter = new TypeParameter
            {
                Id = 10000 + gp.Index,
                Name = names.String(_md.GetString(gp.Name)),
                Variance = TypeParameter.Types.Variance.Inv,
                Reified = semanticReified?.Contains(gp.Index) == true,
            };
            foreach (var constraint in KotlinNominalConstraints(_md, gp))
            {
                parameter.UpperBound.Add(signatures.DecodeEntity(constraint.Type, context, platform: false));
            }
            function.TypeParameter.Add(parameter);
        }
        RestoreErasedMethodBounds(methodHandle, function.TypeParameter, signatures);
    }

    private void RestoreErasedMethodBounds(
        MethodDefinitionHandle methodHandle,
        Google.Protobuf.Collections.RepeatedField<TypeParameter> parameters,
        SignatureDecoder signatures,
        ImmutableArray<KType> ownerArguments = default,
        MetadataAttributes? attributes = null)
    {
        attributes ??= _attrs;
        using var document = attributes.CarrierDocument(
            methodHandle, MetadataAttributes.DotKtNs + "KotlinTypeParameterBoundsAttribute");
        if (document is null) return;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1
            || !root.TryGetProperty("bounds", out var bounds) || bounds.ValueKind != JsonValueKind.Object
            || !bounds.EnumerateObject().Any())
            throw new InvalidDataException("malformed [KotlinTypeParameterBounds] payload");

        var seen = new HashSet<int>();
        foreach (var entry in bounds.EnumerateObject())
        {
            if (!int.TryParse(entry.Name, out var index) || index < 0 || !seen.Add(index)
                || entry.Value.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("malformed [KotlinTypeParameterBounds] entry");
            var parameter = parameters.FirstOrDefault(candidate => candidate.Id == 10000 + index)
                ?? throw new InvalidDataException(
                    "[KotlinTypeParameterBounds] index exceeds method generic arity");
            var restored = entry.Value.EnumerateArray().Select(boundElement =>
            {
                var node = TypeNode.Read(boundElement);
                var bound = signatures.FromTypeNode(node);
                return ownerArguments.IsDefaultOrEmpty
                    ? bound
                    : SubstituteTypeParameters(bound, ownerArguments);
            }).ToArray();
            if (restored.Length == 0)
                throw new InvalidDataException("empty [KotlinTypeParameterBounds] constraint list");
            parameter.UpperBound.Clear();
            parameter.UpperBound.Add(restored);
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
        return _localDefinitions.TryGetValue(name, out var handle) ? handle : null;
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
        var function = new Function
        {
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
            function.ValueParameter.Add(new ValueParameter
            {
                Name = names.String("captureContext"),
                Type = signatures.NamedType("kotlin.Boolean"),
            });
        function.FunctionAnnotation.Add(new Annotation
        {
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
            self.Argument.Add(new KType.Types.Argument
            {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = new KType
                {
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
        iterator.Argument.Add(new KType.Types.Argument
        {
            Projection = KType.Types.Argument.Types.Projection.Inv,
            Type = element,
        });
        result.Function.Add(new Function
        {
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
        foreach (var family in methods.GroupBy(x =>
        {
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
        annotations.Add(new Annotation
        {
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
                    annotation.Add(new Annotation
                    {
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

    private static readonly Dictionary<string, string> OperatorNames = new(StringComparer.Ordinal)
    {
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
            var function = new Function
            {
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
                var tp = new TypeParameter
                {
                    Id = 10000 + gp.Index,
                    Name = names.String(_md.GetString(gp.Name)),
                    Variance = TypeParameter.Types.Variance.Inv,
                };
                foreach (var constraint in KotlinNominalConstraints(_md, gp))
                {
                    tp.UpperBound.Add(signatures.DecodeEntity(constraint.Type, context, platform: false));
                }
                function.TypeParameter.Add(tp);
            }
            function.FunctionAnnotation.Add(ClrExternalAnnotation(names, owner));
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

    private void ReadCSharp14StaticExtensions(
        TypeDefinitionHandle owner,
        Package package,
        NameTable names,
        SignatureDecoder signatures)
    {
        if (!_csharp14Extensions.TryGetContainer(owner, out var container)) return;
        var projectedFunctions = new List<ProjectedFunction>();
        foreach (var entry in container.Functions)
        {
            var method = _md.GetMethodDefinition(entry.KotlinImplementation);
            if (!IsPublicOrProtected(method.Attributes)) continue;
            var declarationIdentity = KotlinDeclarationIdentityCarrier(entry.KotlinImplementation);
            var typeParameterIds = new Dictionary<GenericParameterHandle, int>();
            var context = new GenericContext(owner, entry.KotlinImplementation, typeParameterIds);
            var signature = method.DecodeSignature(signatures, context);
            var kotlinFlags = _attrs.Int32(
                entry.KotlinImplementation, MetadataAttributes.DotKtNs + "KotlinFunctionAttribute") ?? 0;
            var function = new Function
            {
                Name = names.String(declarationIdentity?.Name ??
                    _md.GetString(_md.GetMethodDefinition(entry.Declaration).Name)),
                Flags = (Flags.Callable(
                    method.Attributes,
                    CallableModality(method.Attributes),
                    kotlinFlags,
                    isInline: _attrs.Has(
                        entry.KotlinImplementation,
                        MetadataAttributes.DotKtNs + "KotlinInlineAttribute")) & ~(1 << 18)) |
                    (1 << 18),
                ReturnType = (kotlinFlags & 4) != 0
                    ? ProjectReturn(
                        entry.KotlinImplementation, method, signature.ReturnType, names, signatures, context)
                    : declarationIdentity?.ReturnType is TypeNode semanticReturn
                        ? signatures.FromTypeNode(semanticReturn)
                        : ProjectReturn(
                            entry.KotlinImplementation, method, signature.ReturnType, names, signatures, context),
                ValueParameter = {
                    Parameters(entry.KotlinImplementation, method, signature.ParameterTypes, names, signatures, context,
                        declarationIdentity?.Parameters,
                        declarationIdentity?.NullableWitnessTypeParameterIndices.Count ?? 0)
                },
                ReceiverType = CSharp14ExtensionReceiver(
                    entry.ReceiverMarker, entry.BlockArity, names, signatures,
                    eraseBlockArguments: entry.KotlinImplementation != entry.Implementation),
            };
            PromoteContextParameters(method, function);
            AddCSharp14MethodTypeParameters(
                entry.KotlinImplementation, method, function.TypeParameter, names, signatures, context,
                declarationIdentity?.SemanticReifiedTypeParameterIndices);
            function.FunctionAnnotation.Add(ClrExternalAnnotation(names, owner));
            if (declarationIdentity is { } identity)
                function.FunctionAnnotation.Add(
                    KotlinDeclarationIdentityAnnotation(names, identity.Id, ""));
            function.Flags |= 1;
            package.Function.Add(function);
            projectedFunctions.Add(new ProjectedFunction(
                entry.KotlinImplementation,
                function,
                PhysicalParameterKeys(method, context)));
        }
        AddNrtParamsOverloadBridges(projectedFunctions, package.Function, names);

        foreach (var entry in container.Properties)
        {
            var getter = _md.GetMethodDefinition(entry.KotlinGetterImplementation);
            if (!IsPublicOrProtected(getter.Attributes)) continue;
            var setterHandle = entry.KotlinSetterImplementation;
            // A non-public setter is not callable from the consuming module represented by this KLIB. Project the
            // external surface as `val`; the defining DLL keeps the physical setter so code compiled in that module
            // retains its source semantics. This also avoids inventing a public Kotlin setter by omitting its flags.
            if (!setterHandle.IsNil &&
                !IsPublicOrProtected(_md.GetMethodDefinition(setterHandle).Attributes))
                setterHandle = default;
            var typeParameterIds = new Dictionary<GenericParameterHandle, int>();
            var property = KotlinAccessorProperty(
                owner,
                entry.Declaration,
                _md.GetString(_md.GetPropertyDefinition(entry.Declaration).Name),
                entry.KotlinGetterImplementation,
                setterHandle,
                names,
                signatures,
                typeParameterIds,
                isStatic: true,
                companionReceiver: CSharp14ExtensionReceiver(
                    entry.ReceiverMarker, entry.BlockArity, names, signatures,
                    eraseBlockArguments: entry.KotlinGetterImplementation != entry.GetterImplementation));
            ApplyCSharp14PropertyStorageFacts(
                property, entry.KotlinGetterImplementation, names);
            property.PropertyAnnotation.Add(ClrExternalAnnotation(names, owner));
            property.Flags |= 1;
            package.Property.Add(property);
        }
    }

    private void ApplyCSharp14PropertyStorageFacts(
        Property property,
        MethodDefinitionHandle getter,
        NameTable names)
    {
        using var document = _attrs.CarrierDocument(
            getter, MetadataAttributes.DotKtNs + "KotlinPropertyStorageAttribute");
        if (document is null) return;
        var root = document.RootElement;
        var entries = root.ValueKind == System.Text.Json.JsonValueKind.Object
            ? root.EnumerateObject().ToArray() : [];
        if (entries.Length != 2 ||
            root.TryGetProperty("owner", out var ownerElement) is false ||
            ownerElement.ValueKind != System.Text.Json.JsonValueKind.String ||
            string.IsNullOrEmpty(ownerElement.GetString()) ||
            root.TryGetProperty("field", out var fieldElement) is false ||
            fieldElement.ValueKind != System.Text.Json.JsonValueKind.String ||
            string.IsNullOrEmpty(fieldElement.GetString()))
            throw new InvalidDataException("malformed [KotlinPropertyStorage] payload");
        var storageOwnerName = ownerElement.GetString()!;
        var storageOwners = _md.TypeDefinitions
            .Where(handle => MetadataTypeName(handle) == storageOwnerName)
            .ToArray();
        if (storageOwners.Length != 1)
            throw new InvalidDataException(
                $"[KotlinPropertyStorage] owner '{storageOwnerName}' resolves {storageOwners.Length} time(s)");
        var fieldName = fieldElement.GetString()!;
        var matches = _md.GetTypeDefinition(storageOwners[0]).GetFields()
            .Where(handle => _md.GetString(_md.GetFieldDefinition(handle).Name) == fieldName)
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException(
                $"[KotlinPropertyStorage] field '{fieldName}' resolves {matches.Length} time(s)");
        var fieldHandle = matches[0];
        var field = _md.GetFieldDefinition(fieldHandle);
        if ((field.Attributes & FieldAttributes.Static) == 0 ||
            (field.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Private)
            throw new InvalidDataException("[KotlinPropertyStorage] must name private static storage");

        if ((field.Attributes & FieldAttributes.Literal) != 0 &&
            CompileTimeValue(field, names) is { } constant)
        {
            property.Flags |= (1 << 11) | (1 << 13); // IS_CONST + HAS_CONSTANT
            property.CompileTimeValue = constant;
        }
        if (_attrs.Has(fieldHandle, MetadataAttributes.DotKtNs + "KotlinLateinitAttribute"))
            property.Flags |= 1 << 12; // IS_LATEINIT
    }

    private KType CSharp14ExtensionReceiver(
        MethodDefinitionHandle markerHandle,
        int blockArity,
        NameTable names,
        SignatureDecoder signatures,
        bool eraseBlockArguments = false)
    {
        var marker = _md.GetMethodDefinition(markerHandle);
        var markerOwner = marker.GetDeclaringType();
        var context = new GenericContext(
            markerOwner,
            markerHandle,
            new Dictionary<GenericParameterHandle, int>());
        var signature = marker.DecodeSignature(signatures, context);
        // Roslyn omits the Param row when the synthetic receiver parameter has an empty metadata name. Its type is
        // still present in the method signature; a nil slot correctly falls back to the marker method's NRT context.
        var parameterHandle = PhysicalParameters(marker).Select(parameter => parameter.Handle).FirstOrDefault();
        var receiver = ProjectType(
            parameterHandle,
            signature.ParameterTypes.Single(),
            markerOwner,
            names,
            signatures,
            context);
        if (!eraseBlockArguments)
            return RebindCSharp14BlockParameters(receiver, blockArity);
        // DotKt's associated classifier is intentionally bare (`companion fun G.foo`, never `G<T>.foo`). The
        // receiver block exists solely so C# can close the standard wrapper from `G<string>.foo()`; it is not a
        // Kotlin callable type-parameter declaration and must not leak an unbound 10000+i id into KLIB.
        receiver.Argument.Clear();
        if (receiver.FlexibleUpperBound is { } upper) upper.Argument.Clear();
        return receiver;
    }

    private static KType RebindCSharp14BlockParameters(KType source, int blockArity)
    {
        var result = source.Clone();
        if (result.HasTypeParameter && result.TypeParameter >= 0 && result.TypeParameter < blockArity)
            result.TypeParameter = 10000 + result.TypeParameter;
        for (var index = 0; index < result.Argument.Count; index++)
            if (result.Argument[index].Type is { } argument)
                result.Argument[index].Type = RebindCSharp14BlockParameters(argument, blockArity);
        if (result.FlexibleUpperBound is { } upper)
            result.FlexibleUpperBound = RebindCSharp14BlockParameters(upper, blockArity);
        return result;
    }

    private void AddCSharp14MethodTypeParameters(
        MethodDefinitionHandle methodHandle,
        MethodDefinition method,
        Google.Protobuf.Collections.RepeatedField<TypeParameter> destination,
        NameTable names,
        SignatureDecoder signatures,
        GenericContext context,
        IReadOnlySet<int>? semanticReified)
    {
        foreach (var gpHandle in method.GetGenericParameters())
        {
            var gp = _md.GetGenericParameter(gpHandle);
            var parameter = new TypeParameter
            {
                Id = 10000 + gp.Index,
                Name = names.String(_md.GetString(gp.Name)),
                Variance = TypeParameter.Types.Variance.Inv,
                Reified = semanticReified?.Contains(gp.Index) == true,
            };
            foreach (var constraint in KotlinNominalConstraints(_md, gp))
            {
                parameter.UpperBound.Add(signatures.DecodeEntity(constraint.Type, context, platform: false));
            }
            destination.Add(parameter);
        }
        RestoreErasedMethodBounds(methodHandle, destination, signatures);
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
            if (_existentialCarriers.Contains(childHandle))
                continue;
            if (_csharp14Extensions.IsInfrastructure(childHandle))
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
            MergeGenericStaticCarrier(childHandle, child, names, signatures);
            parent.NestedClassName.Add(names.String(KotlinDefinitionPath(childHandle).Chain[^1]));
            fragment.Class.Add(child);
            fragment.ClassName.Add(child.FqName);
            AddCompanion(childHandle, child, fragment, names, signatures);
            ReadNestedClasses(childHandle, child, fragment, names, signatures);
        }
    }

    private void MergeGenericStaticCarrier(
        TypeDefinitionHandle semanticOwner,
        Class declaration,
        NameTable names,
        SignatureDecoder signatures)
    {
        if (!_genericStaticCarrierByOwner.TryGetValue(semanticOwner, out var carrierHandle)) return;
        var carrier = ReadClass(carrierHandle, _md.GetTypeDefinition(carrierHandle), names, signatures);
        declaration.Function.Add(carrier.Function);
        declaration.Property.Add(carrier.Property);
    }

    private void ValidateGenericStaticCarrierMembers(TypeDefinitionHandle handle)
    {
        var definition = _md.GetTypeDefinition(handle);
        foreach (var fieldHandle in definition.GetFields())
            if ((_md.GetFieldDefinition(fieldHandle).Attributes & FieldAttributes.Static) == 0)
                throw new InvalidDataException(
                    $"KotlinStaticCarrier '{MetadataTypeName(handle)}' contains an instance field");
        foreach (var methodHandle in definition.GetMethods())
            if ((_md.GetMethodDefinition(methodHandle).Attributes & MethodAttributes.Static) == 0)
                throw new InvalidDataException(
                    $"KotlinStaticCarrier '{MetadataTypeName(handle)}' contains an instance method or constructor");
        foreach (var propertyHandle in definition.GetProperties())
        {
            var accessors = _md.GetPropertyDefinition(propertyHandle).GetAccessors();
            var methods = new[] { accessors.Getter, accessors.Setter }
                .Where(method => !method.IsNil).ToArray();
            if (methods.Length == 0 || methods.Any(method =>
                    (_md.GetMethodDefinition(method).Attributes & MethodAttributes.Static) == 0))
                throw new InvalidDataException(
                    $"KotlinStaticCarrier '{MetadataTypeName(handle)}' contains a non-static property");
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
        var accessorPairs = KotlinAccessorPairs(handle, def, typeParameterIds, requireStatic: true);
        var customFieldAccessors = CustomFieldAccessors(def, requireStatic: true);
        var kotlinPropertyAccessorMethods = accessorPairs
            .SelectMany(x => new[] { x.Getter, x.Setter }.Where(method => !method.IsNil))
            .ToHashSet();
        var customPropertyAccessorMethods = customFieldAccessors.Values
            .SelectMany(x => x.Handles)
            .ToHashSet();
        // Every MethodSemantics accessor is excluded from the function surface. Only extension/context pairs and
        // field-backed custom accessors are excluded from the ordinary CLR Property-row projection: a receiverless
        // computed property is owned by that row and must not disappear merely because its accessor has no parameters.
        var methodAccessorMethods = kotlinPropertyAccessorMethods
            .Concat(def.GetProperties().SelectMany(propertyHandle =>
            {
                var accessors = _md.GetPropertyDefinition(propertyHandle).GetAccessors();
                return new[] { accessors.Getter, accessors.Setter }.Where(method => !method.IsNil);
            }))
            .Concat(customPropertyAccessorMethods)
            .ToHashSet();
        var separatelyProjectedPropertyAccessors = kotlinPropertyAccessorMethods
            .Concat(customPropertyAccessorMethods)
            .ToHashSet();
        foreach (var pair in accessorPairs)
        {
            var representative = pair.Getter.IsNil ? pair.Setter : pair.Getter;
            var representativeRole = pair.Getter.IsNil ? "set" : "get";
            var companion = CompanionExtension(representative, signatures, representativeRole);
            var setterCompanion = pair.Setter.IsNil ? null : CompanionExtension(pair.Setter, signatures, "set");
            if (!pair.Setter.IsNil && ((companion is null) != (setterCompanion is null) ||
                (companion is not null && setterCompanion is not null &&
                    (companion.Name != setterCompanion.Name || !companion.Receiver.Equals(setterCompanion.Receiver))))
            )
                throw new InvalidDataException("inconsistent companion-extension accessor carriers");
            var property = KotlinAccessorProperty(handle, pair.Declaration, pair.Name,
                pair.Getter, pair.Setter, names, signatures, typeParameterIds,
                isStatic: companion is not null, companionReceiver: companion?.Receiver);
            if (companion is not null)
            {
                property.Name = names.String(companion.Name);
            }
            property.PropertyAnnotation.Add(ClrExternalAnnotation(names, handle));
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
            var declarationIdentity = KotlinDeclarationIdentityCarrier(methodHandle);
            var name = declarationIdentity?.Name ?? _md.GetString(method.Name);
            if (!IsPublicOrProtected(method.Attributes) || name is ".ctor" or ".cctor" ||
                (method.Attributes & MethodAttributes.SpecialName) != 0 || name.StartsWith('<') ||
                methodAccessorMethods.Contains(methodHandle))
                continue;
            var context = new GenericContext(handle, methodHandle, typeParameterIds);
            var sig = method.DecodeSignature(signatures, context);
            var modality = (method.Attributes & MethodAttributes.Abstract) != 0 ? 2
                : (method.Attributes & MethodAttributes.Virtual) != 0 && (method.Attributes & MethodAttributes.Final) == 0 ? 1 : 0;
            var companion = CompanionExtension(methodHandle, signatures, "function");
            var sourceMethodName = KotlinSourceMethodName(methodHandle);
            var kotlinFlags = _attrs.Int32(
                methodHandle, MetadataAttributes.DotKtNs + "KotlinFunctionAttribute") ?? 0;
            var function = new Function
            {
                Name = names.String(companion?.Name ?? sourceMethodName ?? name),
                Flags = Flags.Callable(method.Attributes, modality,
                    kotlinFlags,
                    isInline: _attrs.Has(methodHandle, MetadataAttributes.DotKtNs + "KotlinInlineAttribute")) & ~(1 << 18),
                ReturnType = (kotlinFlags & 4) != 0
                    ? ProjectReturn(methodHandle, method, sig.ReturnType, names, signatures, context)
                    : declarationIdentity?.ReturnType is TypeNode semanticReturn
                        ? signatures.FromTypeNode(semanticReturn)
                        : ProjectReturn(methodHandle, method, sig.ReturnType, names, signatures, context),
                ValueParameter = { Parameters(methodHandle, method, sig.ParameterTypes, names, signatures, context,
                    declarationIdentity?.Parameters,
                    declarationIdentity?.NullableWitnessTypeParameterIndices.Count ?? 0) },
            };
            PromoteContextParameters(method, function);
            // A Kotlin 2.4 COMPANION EXTENSION (`companion fun C.foo()`) is physically an ordinary receiverless static
            // of this facade; the trusted carrier holds the type it is associated with. Restoring it needs no new
            // encoding — a static callable with a receiver IS the standard shape (`isStatic && receiverParameter`), so
            // put the static flag back and take the receiver from the carrier instead of from a physical parameter,
            // which a companion extension does not have.
            if (companion is not null)
            {
                function.Flags |= 1 << 18;
                function.ReceiverType = companion.Receiver;
            }
            else PromoteReceiver(methodHandle, method, function);
            foreach (var gpHandle in method.GetGenericParameters())
            {
                var gp = _md.GetGenericParameter(gpHandle);
                var tp = new TypeParameter
                {
                    Id = 10000 + gp.Index,
                    Name = names.String(_md.GetString(gp.Name)),
                    Variance = TypeParameter.Types.Variance.Inv,
                    Reified = declarationIdentity?.SemanticReifiedTypeParameterIndices.Contains(gp.Index) == true,
                };
                foreach (var constraint in KotlinNominalConstraints(_md, gp))
                {
                    tp.UpperBound.Add(signatures.DecodeEntity(constraint.Type, context, platform: false));
                }
                function.TypeParameter.Add(tp);
            }
            RestoreErasedMethodBounds(methodHandle, function.TypeParameter, signatures);
            ApplyPInvokeProjection(method, function, names);
            function.FunctionAnnotation.Add(ClrExternalAnnotation(names, handle));
            if (declarationIdentity is { } identity)
                function.FunctionAnnotation.Add(
                    KotlinDeclarationIdentityAnnotation(names, identity.Id, ""));
            function.Flags |= 1;
            package.Function.Add(function);
        }

        // Only receiverless Property rows suppress a same-named public field. Extension/context accessor pairs are
        // overloads of an independent receiverless field-backed property, not alternate accessors for that field.
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var propertyHandle in def.GetProperties())
        {
            var property = _md.GetPropertyDefinition(propertyHandle);
            var accessors = property.GetAccessors();
            var methodHandle = !accessors.Getter.IsNil ? accessors.Getter : accessors.Setter;
            if (methodHandle.IsNil) continue;
            // Kotlin ordinary-method accessors were already projected above from this exact MethodSemantics row.
            // The remaining loop is for CLR special-name properties. Projecting the same row twice creates a second
            // receiverless Kotlin property and makes every context/extension use ambiguous.
            if (separatelyProjectedPropertyAccessors.Contains(methodHandle)) continue;
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
            // The CLR-property twin of the facade field/method paths: a companion extension that emits a real property
            // row (a delegated one) is still a static declaration whose receiver comes from the carrier.
            var representativeRole = accessors.Getter.IsNil ? "set" : "get";
            var propCompanion = CompanionExtension(methodHandle, signatures, representativeRole);
            var propSetterCompanion = accessors.Setter.IsNil
                ? null
                : CompanionExtension(accessors.Setter, signatures, "set");
            if (!accessors.Setter.IsNil && ((propCompanion is null) != (propSetterCompanion is null) ||
                (propCompanion is not null && propSetterCompanion is not null &&
                    (propCompanion.Name != propSetterCompanion.Name ||
                        !propCompanion.Receiver.Equals(propSetterCompanion.Receiver))))
            )
                throw new InvalidDataException("inconsistent companion-extension property carriers");
            var projected = new Property
            {
                Name = names.String(propCompanion?.Name ?? KotlinPropertySourceName(property, accessors)),
                ReturnType = type,
                Flags = Flags.Property(method.Attributes, canWrite, isStatic: propCompanion is not null),
                SetterValueParameter = canWrite ? new ValueParameter { Name = names.String("value"), Type = type.Clone() } : null,
            };
            if (propCompanion is not null)
            {
                projected.ReceiverType = propCompanion.Receiver;
            }
            else if (!_attrs.IsDotKtAssembly && sig.ParameterTypes.Length != 0)
            {
                var receiverHandle = method.GetParameters()
                    .Select(h => (Handle: h, Row: _md.GetParameter(h)))
                    .First(x => x.Row.SequenceNumber == 1).Handle;
                projected.ReceiverType = ProjectType(receiverHandle, sig.ParameterTypes[0], handle, names, signatures, context);
            }
            ApplyAccessorFlags(projected, accessors.Getter, accessors.Setter);
            var getterIdentity = accessors.Getter.IsNil
                ? null
                : KotlinDeclarationIdentityCarrier(accessors.Getter)?.Id;
            var setterIdentity = accessors.Setter.IsNil
                ? null
                : KotlinDeclarationIdentityCarrier(accessors.Setter)?.Id;
            projected.PropertyAnnotation.Add(ClrExternalAnnotation(names, handle));
            if (getterIdentity is not null || setterIdentity is not null)
                projected.PropertyAnnotation.Add(KotlinDeclarationIdentityAnnotation(
                    names, getterIdentity ?? "", setterIdentity ?? ""));
            projected.Flags |= 1;
            package.Property.Add(projected);
            propertyNames.Add(KotlinPropertySourceName(property, accessors));
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
            // `companion val C.bar = 1` stores into a plain static field of this facade; its carrier restores both the
            // static declaration flag and the associated type, exactly as the method path above does.
            var fieldCompanion = CompanionExtension(fieldHandle, signatures, "field");
            var projected = new Property
            {
                Name = names.String(fieldCompanion?.Name ?? name),
                ReturnType = type,
                Flags = fieldCompanion is null
                    ? Flags.Property(field.Attributes, canWrite) & ~(1 << 19)
                    : Flags.Property(field.Attributes, canWrite),
                ReceiverType = fieldCompanion?.Receiver,
                SetterValueParameter = canWrite ? new ValueParameter { Name = names.String("value"), Type = type.Clone() } : null,
            };
            if ((field.Attributes & FieldAttributes.Literal) != 0 &&
                CompileTimeValue(field, names) is { } constant)
            {
                projected.Flags |= (1 << 11) | (1 << 13); // IS_CONST + HAS_CONSTANT
                projected.CompileTimeValue = constant;
            }
            var isLateinit = _attrs.Has(fieldHandle, MetadataAttributes.DotKtNs + "KotlinLateinitAttribute");
            if (isLateinit)
                projected.Flags |= 1 << 12; // IS_LATEINIT
            projected.PropertyAnnotation.Add(ClrExternalAnnotation(names, handle));
            if (hasCustomAccessors)
                ApplyAccessorFlags(projected, custom.Handles);
            else
            {
                projected.PropertyAnnotation.Add(ClrFieldAnnotation(names));
                if (isLateinit) projected.PropertyAnnotation.Add(ClrLateinitFieldAnnotation(names));
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
        foreach (var propertyHandle in def.GetProperties())
        {
            var property = _md.GetPropertyDefinition(propertyHandle);
            var propertyName = _md.GetString(property.Name);
            if (!fields.Contains(propertyName)) continue;
            var accessors = property.GetAccessors();
            // Same-name extension/context properties are independent overloads, not custom accessors for this field.
            // A getter owns no value slot; a setter owns exactly its final value slot when the Property is receiverless.
            int ParameterCount(MethodDefinitionHandle handle) => _md.GetMethodDefinition(handle).GetParameters()
                .Count(parameter => _md.GetParameter(parameter).SequenceNumber != 0);
            var receiverless = !accessors.Getter.IsNil
                ? ParameterCount(accessors.Getter) == 0
                : !accessors.Setter.IsNil && ParameterCount(accessors.Setter) == 1;
            if (!receiverless) continue;
            var handles = new[] { accessors.Getter, accessors.Setter }.Where(handle => !handle.IsNil).ToList();
            var accepted = handles.Where(handle =>
            {
                var method = _md.GetMethodDefinition(handle);
                return IsPublicOrProtected(method.Attributes)
                    && (!requireStatic || (method.Attributes & MethodAttributes.Static) != 0);
            }).ToList();
            var access = (!accessors.Getter.IsNil && accepted.Contains(accessors.Getter) ? 1 : 0)
                | (!accessors.Setter.IsNil && accepted.Contains(accessors.Setter) ? 2 : 0);
            if (access == 0) continue;
            if (!result.TryGetValue(propertyName, out var existing))
                existing = (0, new List<MethodDefinitionHandle>());
            existing.Access |= access;
            existing.Handles.AddRange(accepted);
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
            var association = PropertyAccessorAssociation(handle);
            if (association?.Kind == 1)
                property.GetterFlags = Flags.Accessor(method.Attributes);
            else if (association?.Kind == 2)
                property.SetterFlags = Flags.Accessor(method.Attributes);
        }
    }

    private readonly Dictionary<MethodDefinitionHandle, (string Name, int Kind)> _propertyAccessorAssociations = new();
    private readonly HashSet<MethodDefinitionHandle> _ambiguousPropertyAccessorAssociations = new();
    private readonly HashSet<TypeDefinitionHandle> _indexedPropertyAccessorTypes = new();

    private (string Name, int Kind)? PropertyAccessorAssociation(MethodDefinitionHandle methodHandle)
    {
        if (methodHandle.IsNil) return null;
        if (KotlinPropertyAccessorCarrier(methodHandle) is { } carrier)
            return (carrier.Name, carrier.Kind);
        var typeHandle = _md.GetMethodDefinition(methodHandle).GetDeclaringType();
        if (typeHandle.IsNil) return null;
        if (_indexedPropertyAccessorTypes.Add(typeHandle))
        {
            void Index(MethodDefinitionHandle accessor, string propertyName, int kind)
            {
                if (accessor.IsNil || _ambiguousPropertyAccessorAssociations.Contains(accessor)) return;
                var association = (propertyName, kind);
                if (_propertyAccessorAssociations.TryGetValue(accessor, out var existing)
                    && existing != association)
                {
                    _propertyAccessorAssociations.Remove(accessor);
                    _ambiguousPropertyAccessorAssociations.Add(accessor);
                }
                else
                    _propertyAccessorAssociations[accessor] = association;
            }

            var type = _md.GetTypeDefinition(typeHandle);
            foreach (var propertyHandle in type.GetProperties())
            {
                var property = _md.GetPropertyDefinition(propertyHandle);
                var accessors = property.GetAccessors();
                var propertyName = _md.GetString(property.Name);
                Index(accessors.Getter, propertyName, 1);
                Index(accessors.Setter, propertyName, 2);
            }
        }
        return _propertyAccessorAssociations.TryGetValue(methodHandle, out var result) ? result : null;
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

    private List<(string Name, PropertyDefinitionHandle Declaration,
        MethodDefinitionHandle Getter, MethodDefinitionHandle Setter)> KotlinAccessorPairs(
        TypeDefinitionHandle owner,
        TypeDefinition def,
        IReadOnlyDictionary<GenericParameterHandle, int> typeParameterIds,
        bool requireStatic = false)
    {
        var result = new List<(string, PropertyDefinitionHandle, MethodDefinitionHandle, MethodDefinitionHandle)>();
        var associatedMethods = new HashSet<MethodDefinitionHandle>();
        foreach (var propertyHandle in def.GetProperties())
        {
            var definition = _md.GetPropertyDefinition(propertyHandle);
            var accessors = definition.GetAccessors();
            var getterHandle = accessors.Getter;
            var setterHandle = accessors.Setter;
            var representativeHandle = getterHandle.IsNil ? setterHandle : getterHandle;
            if (representativeHandle.IsNil) continue;
            var representative = _md.GetMethodDefinition(representativeHandle);
            if ((representative.Attributes & MethodAttributes.SpecialName) != 0 ||
                !IsPublicOrProtected(representative.Attributes) ||
                requireStatic && (representative.Attributes & MethodAttributes.Static) == 0)
                continue;
            var physical = SemanticPhysicalParameters(representativeHandle, representative);
            // A setter-only Property row represents a semantic `var` whose getter is inherited. The final semantic
            // parameter is the value slot and is not part of the Kotlin receiver/context prefix.
            var propertyPhysical = getterHandle.IsNil ? physical.Take(physical.Count - 1).ToList() : physical;
            var hasReceiver = HasKotlinExtensionReceiver(representativeHandle);
            // A COMPANION EXTENSION property accessor carries its receiver in trusted metadata instead of a physical
            // receiver slot (the frontend drops the parameter), so a receiverless zero-argument getter IS an accessor
            // here when the carrier says so. Without this the associated getter would surface as a plain function.
            var hasCompanionReceiver =
                _attrs.Has(representativeHandle, MetadataAttributes.DotKtNs + "KotlinCompanionExtensionAttribute");
            var contextStart = hasReceiver ? 1 : 0;
            if (!hasReceiver && !hasCompanionReceiver && propertyPhysical.Count == 0 ||
                propertyPhysical.Skip(contextStart).Any(x =>
                    !_attrs.Has(x.Handle, MetadataAttributes.DotKtNs + "KotlinContextParameterAttribute")))
                continue;
            MethodSignature<string>? getterSignature = getterHandle.IsNil
                ? null
                : _md.GetMethodDefinition(getterHandle).DecodeSignature(
                    RawSignatureTypeProvider.Instance,
                    new GenericContext(owner, getterHandle, typeParameterIds));
            // Context parameters participate in Kotlin property overload resolution. Parameter count alone can pair
            // `context(A) var C.p`'s setter with `context(B) val C.p`'s getter, silently making the latter writable.
            // Compare the complete physical context/receiver prefix and the setter value against the getter result.
            if (!getterHandle.IsNil && !setterHandle.IsNil)
            {
                var setter = _md.GetMethodDefinition(setterHandle);
                if ((setter.Attributes & MethodAttributes.SpecialName) != 0 ||
                    !IsPublicOrProtected(setter.Attributes) ||
                    (setter.Attributes & MethodAttributes.Static) !=
                        (representative.Attributes & MethodAttributes.Static))
                {
                    setterHandle = default;
                }
                else
                {
                    if (HasKotlinExtensionReceiver(getterHandle) != HasKotlinExtensionReceiver(setterHandle))
                        throw new InvalidDataException(
                            "Kotlin property getter/setter disagree about the extension-receiver role");
                    var setterSignature = setter.DecodeSignature(
                    RawSignatureTypeProvider.Instance,
                    new GenericContext(owner, setterHandle, typeParameterIds));
                    var getterParameterCount = SemanticParameterCount(
                        getterHandle, getterSignature!.Value.ParameterTypes.Length);
                    var setterParameterCount = SemanticParameterCount(
                        setterHandle, setterSignature.ParameterTypes.Length);
                    if (getterSignature.Value.GenericParameterCount != setterSignature.GenericParameterCount ||
                        setterParameterCount != getterParameterCount + 1 ||
                        !getterSignature.Value.ParameterTypes.Take(getterParameterCount).SequenceEqual(
                            setterSignature.ParameterTypes.Take(getterParameterCount),
                            StringComparer.Ordinal) ||
                        getterSignature.Value.ReturnType != setterSignature.ParameterTypes[setterParameterCount - 1])
                        setterHandle = default;
                }
            }
            result.Add((KotlinPropertySourceName(definition, accessors),
                propertyHandle, getterHandle, setterHandle));
            if (!getterHandle.IsNil) associatedMethods.Add(getterHandle);
            if (!setterHandle.IsNil) associatedMethods.Add(setterHandle);
        }

        // Method-generic extension accessors cannot be associated through a CLR Property row because that row has no
        // owner for `!!T`. A signature-changing MethodImpl bridge can likewise own the one physical Property row while
        // its source accessor remains the Kotlin declaration. Consume the exact trusted carriers written by bir2cir
        // and pair only by their opaque associations; physical names and erased signatures are irrelevant.
        var carrierGroups = new Dictionary<string,
            (string? Name, MethodDefinitionHandle Getter, MethodDefinitionHandle Setter)>(StringComparer.Ordinal);
        var carrierMethods = def.GetMethods()
            .Select(methodHandle => (Method: methodHandle, Carrier: KotlinPropertyAccessorCarrier(methodHandle)))
            .Where(item => item.Carrier is not null).ToArray();
        var bridgeSourceAssociations = carrierMethods
            .Select(item => item.Carrier!.Value.SourceAssociation)
            .Where(association => association is not null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (methodHandle, carrierValue) in carrierMethods)
        {
            if (associatedMethods.Contains(methodHandle)) continue;
            var carrier = carrierValue!.Value;
            var method = _md.GetMethodDefinition(methodHandle);
            if ((method.Attributes & MethodAttributes.SpecialName) != 0 ||
                !IsPublicOrProtected(method.Attributes) ||
                requireStatic && (method.Attributes & MethodAttributes.Static) == 0)
                continue;
            carrierGroups.TryGetValue(carrier.Association, out var pair);
            if (pair.Name is not null && pair.Name != carrier.Name)
                throw new InvalidDataException("Kotlin property accessor association has inconsistent source names");
            pair.Name = carrier.Name;
            if (carrier.Kind == 1)
            {
                if (!pair.Getter.IsNil)
                    throw new InvalidDataException("Kotlin property accessor association has duplicate getters");
                pair.Getter = methodHandle;
            }
            else
            {
                if (!pair.Setter.IsNil)
                    throw new InvalidDataException("Kotlin property accessor association has duplicate setters");
                pair.Setter = methodHandle;
            }
            carrierGroups[carrier.Association] = pair;
        }
        foreach (var (association, pair) in carrierGroups)
        {
            var representativeHandle = pair.Getter.IsNil ? pair.Setter : pair.Getter;
            if (representativeHandle.IsNil || pair.Name is null) continue;
            var representative = _md.GetMethodDefinition(representativeHandle);
            var representativeSignature = representative.DecodeSignature(
                RawSignatureTypeProvider.Instance,
                new GenericContext(owner, representativeHandle, typeParameterIds));
            if (representativeSignature.GenericParameterCount == 0
                && !bridgeSourceAssociations.Contains(association))
                throw new InvalidDataException(
                    $"[KotlinPropertyAccessor] without a Property row requires a method-generic accessor: " +
                    $"{MetadataTypeName(owner)}::{_md.GetString(representative.Name)}");
            if (!pair.Getter.IsNil && !pair.Setter.IsNil)
            {
                var getter = _md.GetMethodDefinition(pair.Getter);
                var setter = _md.GetMethodDefinition(pair.Setter);
                if (HasKotlinExtensionReceiver(pair.Getter) != HasKotlinExtensionReceiver(pair.Setter))
                    throw new InvalidDataException(
                        "[KotlinPropertyAccessor] getter/setter disagree about the extension-receiver role");
                var getterSignature = getter.DecodeSignature(
                    RawSignatureTypeProvider.Instance,
                    new GenericContext(owner, pair.Getter, typeParameterIds));
                var setterSignature = setter.DecodeSignature(
                    RawSignatureTypeProvider.Instance,
                    new GenericContext(owner, pair.Setter, typeParameterIds));
                var getterParameterCount = SemanticParameterCount(
                    pair.Getter, getterSignature.ParameterTypes.Length);
                var setterParameterCount = SemanticParameterCount(
                    pair.Setter, setterSignature.ParameterTypes.Length);
                if ((getter.Attributes & MethodAttributes.Static) != (setter.Attributes & MethodAttributes.Static)
                    || getterSignature.GenericParameterCount != setterSignature.GenericParameterCount
                    || setterParameterCount != getterParameterCount + 1
                    || !getterSignature.ParameterTypes.Take(getterParameterCount).SequenceEqual(
                        setterSignature.ParameterTypes.Take(getterParameterCount),
                        StringComparer.Ordinal)
                    || getterSignature.ReturnType != setterSignature.ParameterTypes[setterParameterCount - 1])
                    throw new InvalidDataException(
                        "[KotlinPropertyAccessor] getter/setter signatures are incompatible");
            }
            var physical = SemanticPhysicalParameters(representativeHandle, representative);
            var propertyPhysical = pair.Getter.IsNil ? physical.Take(physical.Count - 1).ToList() : physical;
            var hasReceiver = HasKotlinExtensionReceiver(representativeHandle);
            var contextStart = hasReceiver ? 1 : 0;
            if (!hasReceiver && propertyPhysical.Count == 0 ||
                propertyPhysical.Skip(contextStart).Any(x =>
                    !_attrs.Has(x.Handle, MetadataAttributes.DotKtNs + "KotlinContextParameterAttribute")))
                continue;
            result.Add((pair.Name, default, pair.Getter, pair.Setter));
        }
        return result;
    }

    private (string Name, int Kind, string Association, string? SourceAssociation)? KotlinPropertyAccessorCarrier(
        MethodDefinitionHandle methodHandle)
    {
        using var document = _attrs.CarrierDocument(
            methodHandle, MetadataAttributes.DotKtNs + "KotlinPropertyAccessorAttribute");
        if (document is null) return null;
        var root = document.RootElement;
        var propertyCount = root.ValueKind == JsonValueKind.Object
            ? root.EnumerateObject().Count() : 0;
        if (root.ValueKind != JsonValueKind.Object ||
            propertyCount is not (3 or 4) ||
            !root.TryGetProperty("name", out var nameNode) || nameNode.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("kind", out var kindNode) || kindNode.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("association", out var associationNode) ||
            associationNode.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("malformed [KotlinPropertyAccessor] payload");
        var hasSourceAssociation = root.TryGetProperty("sourceAssociation", out var sourceAssociationNode);
        if (hasSourceAssociation != (propertyCount == 4) || hasSourceAssociation &&
            (sourceAssociationNode.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(sourceAssociationNode.GetString())))
            throw new InvalidDataException("malformed [KotlinPropertyAccessor] source association");
        var name = nameNode.GetString();
        var association = associationNode.GetString();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(association))
            throw new InvalidDataException("empty [KotlinPropertyAccessor] identity");
        var kind = kindNode.GetString() switch
        {
            "get" => 1,
            "set" => 2,
            _ => throw new InvalidDataException("invalid [KotlinPropertyAccessor] role"),
        };
        return (name, kind, association, hasSourceAssociation ? sourceAssociationNode.GetString() : null);
    }

    private string KotlinPropertySourceName(PropertyDefinition property, PropertyAccessors accessors)
    {
        var carriedNames = new[] { accessors.Getter, accessors.Setter }
            .Where(handle => !handle.IsNil)
            .Select(KotlinPropertyAccessorCarrier)
            .Where(carrier => carrier is not null)
            .Select(carrier => carrier!.Value.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (carriedNames.Length > 1)
            throw new InvalidDataException("Property accessors carry inconsistent Kotlin source names");
        return carriedNames.Length == 1 ? carriedNames[0] : _md.GetString(property.Name);
    }

    private string? KotlinSourceMethodName(MethodDefinitionHandle methodHandle)
    {
        using var document = _attrs.CarrierDocument(
            methodHandle, MetadataAttributes.DotKtNs + "KotlinSourceMethodAttribute");
        if (document is null) return null;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
            !root.TryGetProperty("name", out var nameNode) || nameNode.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(nameNode.GetString()))
            throw new InvalidDataException("malformed [KotlinSourceMethod] payload");
        return nameNode.GetString();
    }

    private sealed record DeclarationIdentityCarrier(
        string Id,
        string Name,
        IReadOnlyList<TypeNode>? Parameters,
        TypeNode? ReturnType,
        IReadOnlySet<int> SemanticReifiedTypeParameterIndices,
        IReadOnlySet<int> NullableWitnessTypeParameterIndices);

    private DeclarationIdentityCarrier? KotlinDeclarationIdentityCarrier(MethodDefinitionHandle methodHandle)
    {
        using var document = _attrs.CarrierDocument(
            methodHandle, MetadataAttributes.DotKtNs + "KotlinDeclarationIdentityAttribute");
        if (document is null) return null;
        var root = document.RootElement;
        var propertyCount = root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().Count() : 0;
        if (propertyCount is < 2 or > 5 ||
            !root.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("name", out var nameNode) || nameNode.ValueKind != JsonValueKind.String ||
            root.EnumerateObject().Any(property => property.Name is not (
                "id" or "name" or "signature" or "reified" or "nullableWitness")) ||
            root.TryGetProperty("signature", out var signatureNode) && signatureNode.ValueKind != JsonValueKind.Object ||
            root.TryGetProperty("reified", out var reifiedNode) && reifiedNode.ValueKind != JsonValueKind.Array ||
            root.TryGetProperty("nullableWitness", out var witnessNode)
                && witnessNode.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("malformed [KotlinDeclarationIdentity] payload");
        var id = idNode.GetString();
        var name = nameNode.GetString();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            throw new InvalidDataException("empty [KotlinDeclarationIdentity] payload");
        var reified = root.TryGetProperty("reified", out reifiedNode)
            ? reifiedNode.EnumerateArray().Select(index => index.ValueKind == JsonValueKind.Number
                && index.TryGetInt32(out var value) && value >= 0
                ? value
                : throw new InvalidDataException("malformed [KotlinDeclarationIdentity] reified index"))
                .ToHashSet()
            : new HashSet<int>();
        if (reified.Any(index => index >= _md.GetMethodDefinition(methodHandle).GetGenericParameters().Count))
            throw new InvalidDataException("[KotlinDeclarationIdentity] reified index exceeds method generic arity");
        var nullableWitness = root.TryGetProperty("nullableWitness", out witnessNode)
            ? witnessNode.EnumerateArray().Select(index => index.ValueKind == JsonValueKind.Number
                && index.TryGetInt32(out var value) && value >= 0
                ? value
                : throw new InvalidDataException(
                    "malformed [KotlinDeclarationIdentity] nullable-witness index"))
                .ToHashSet()
            : new HashSet<int>();
        if (nullableWitness.Any(index =>
            index >= _md.GetMethodDefinition(methodHandle).GetGenericParameters().Count))
            throw new InvalidDataException(
                "[KotlinDeclarationIdentity] nullable-witness index exceeds method generic arity");
        if (!root.TryGetProperty("signature", out signatureNode))
        {
            if (nullableWitness.Count != 0)
                throw new InvalidDataException(
                    "[KotlinDeclarationIdentity] nullable-witness indices require a semantic signature");
            return new DeclarationIdentityCarrier(id, name, null, null, reified, nullableWitness);
        }
        if (signatureNode.EnumerateObject().Count() != 2 ||
            !signatureNode.TryGetProperty("params", out var paramsNode) || paramsNode.ValueKind != JsonValueKind.Array ||
            !signatureNode.TryGetProperty("ret", out var retNode) || TypeNode.Read(retNode) is not { } returnType)
            throw new InvalidDataException("malformed [KotlinDeclarationIdentity] semantic signature");
        var parameters = paramsNode.EnumerateArray().Select(parameter =>
            TypeNode.Read(parameter) ?? throw new InvalidDataException(
                "malformed [KotlinDeclarationIdentity] semantic parameter type")).ToArray();
        return new DeclarationIdentityCarrier(id, name, parameters, returnType, reified, nullableWitness);
    }

    /// `isStatic` is the caller's, because the two call sites read it from different places: a CLASS member accessor
    /// is a Kotlin static exactly when its CLR accessor is static (a `companion { }` property), while a FILE-FACADE
    /// accessor is static only when the trusted companion-extension carrier says so — every ordinary top-level
    /// property has a static accessor and is not a static declaration.
    private Property KotlinAccessorProperty(
        TypeDefinitionHandle owner,
        PropertyDefinitionHandle propertyHandle,
        string propertyName,
        MethodDefinitionHandle getterHandle,
        MethodDefinitionHandle setterHandle,
        NameTable names,
        SignatureDecoder signatures,
        Dictionary<GenericParameterHandle, int> typeParameterIds,
        bool isStatic = false,
        KType? companionReceiver = null)
    {
        var representativeHandle = getterHandle.IsNil ? setterHandle : getterHandle;
        if (representativeHandle.IsNil)
            throw new InvalidDataException($"Kotlin accessor property '{propertyName}' has no accessor");
        var representative = _md.GetMethodDefinition(representativeHandle);
        var declarationIdentity = KotlinDeclarationIdentityCarrier(representativeHandle);
        var semanticPropertyName = PropertyAccessorAssociation(representativeHandle)?.Name ?? propertyName;
        var physical = SemanticPhysicalParameters(representativeHandle, representative);
        var propertyPhysical = getterHandle.IsNil ? physical.Take(physical.Count - 1).ToList() : physical;
        var hasReceiver = HasKotlinExtensionReceiver(representativeHandle);
        var context = new GenericContext(owner, representativeHandle, typeParameterIds);
        var propertySignature = propertyHandle.IsNil
            ? representative.DecodeSignature(signatures, context)
            : _md.GetPropertyDefinition(propertyHandle).DecodeSignature(signatures, context);
        var propertyTypeSignature = propertyHandle.IsNil && getterHandle.IsNil
            ? propertySignature.ParameterTypes[physical.Count - 1]
            : propertySignature.ReturnType;
        var type = declarationIdentity is { ReturnType: { } semanticReturn } && !getterHandle.IsNil
            ? signatures.FromTypeNode(semanticReturn)
            : declarationIdentity?.Parameters is { Count: > 0 } setterSemanticParameters && getterHandle.IsNil
                ? signatures.FromTypeNode(setterSemanticParameters[^1])
                : !getterHandle.IsNil
                    ? ProjectReturn(getterHandle, representative, propertyTypeSignature,
                        names, signatures, context)
                    : ProjectType(
                        physical[^1].Handle, propertyTypeSignature, owner, names, signatures, context);
        var property = new Property
        {
            // The CLR Property row may have a different physical name when two Kotlin properties erase to the same
            // CLI signature. #397's exact accessor association owns the semantic property spelling; never leak or
            // reverse-parse either the physical Property name or the accessor MethodDef name into KLIB.
            Name = names.String(semanticPropertyName),
            ReturnType = type,
            Flags = Flags.Property(representative.Attributes, !setterHandle.IsNil, isStatic),
            SetterValueParameter = setterHandle.IsNil
                ? null
                : new ValueParameter { Name = names.String("value"), Type = type.Clone() },
        };
        ApplyAccessorFlags(property, getterHandle, setterHandle);
        var getterIdentity = getterHandle.IsNil ? null : KotlinDeclarationIdentityCarrier(getterHandle)?.Id;
        var setterIdentity = setterHandle.IsNil ? null : KotlinDeclarationIdentityCarrier(setterHandle)?.Id;
        if (getterIdentity is not null || setterIdentity is not null)
            property.PropertyAnnotation.Add(KotlinDeclarationIdentityAnnotation(
                names, getterIdentity ?? "", setterIdentity ?? ""));
        var contextStart = 0;
        // A COMPANION EXTENSION has no physical receiver slot at all — the frontend drops it — so its associated
        // type comes from the carrier rather than from a leading physical receiver parameter.
        if (companionReceiver is not null)
            property.ReceiverType = companionReceiver;
        else if (hasReceiver)
        {
            property.ReceiverType = declarationIdentity?.Parameters is { Count: > 0 } receiverSemanticParameters
                ? signatures.FromTypeNode(receiverSemanticParameters[0])
                : ProjectType(
                    propertyPhysical[0].Handle, propertySignature.ParameterTypes[0], owner, names, signatures, context);
            contextStart = 1;
        }
        for (var i = contextStart; i < propertyPhysical.Count; i++)
        {
            property.ContextParameter.Add(new ValueParameter
            {
                Name = names.String(propertyPhysical[i].Row.Name.IsNil
                    ? $"context{i - contextStart}"
                    : _md.GetString(propertyPhysical[i].Row.Name)),
                Type = declarationIdentity?.Parameters is { } contextSemanticParameters
                    ? signatures.FromTypeNode(contextSemanticParameters[i])
                    : ProjectType(
                        propertyPhysical[i].Handle, propertySignature.ParameterTypes[i], owner, names, signatures, context),
            });
        }
        foreach (var gpHandle in representative.GetGenericParameters())
        {
            var gp = _md.GetGenericParameter(gpHandle);
            var parameter = new TypeParameter
            {
                Id = 10000 + gp.Index,
                Name = names.String(_md.GetString(gp.Name)),
                Variance = TypeParameter.Types.Variance.Inv,
                Reified = declarationIdentity?.SemanticReifiedTypeParameterIndices.Contains(gp.Index) == true,
            };
            foreach (var constraint in KotlinNominalConstraints(_md, gp))
            {
                parameter.UpperBound.Add(signatures.DecodeEntity(constraint.Type, context, platform: false));
            }
            property.TypeParameter.Add(parameter);
        }
        RestoreErasedMethodBounds(representativeHandle, property.TypeParameter, signatures);
        return property;
    }

    private List<(ParameterHandle Handle, Parameter Row)> PhysicalParameters(MethodDefinition method) =>
        method.GetParameters()
            .Select(h => (Handle: h, Row: _md.GetParameter(h)))
            .Where(x => x.Row.SequenceNumber > 0)
            .OrderBy(x => x.Row.SequenceNumber)
            .ToList();

    private List<(ParameterHandle Handle, Parameter Row)> SemanticPhysicalParameters(
        MethodDefinitionHandle methodHandle,
        MethodDefinition method)
    {
        var physical = PhysicalParameters(method);
        return physical.Take(SemanticParameterCount(methodHandle, physical.Count)).ToList();
    }

    private void ValidateKotlinExtensionReceiverCarriers()
    {
        var carrier = MetadataAttributes.DotKtNs + "KotlinExtensionReceiverAttribute";
        foreach (var methodHandle in _md.MethodDefinitions)
        {
            var method = _md.GetMethodDefinition(methodHandle);
            var allRows = method.GetParameters()
                .Select(handle => (Handle: handle, Row: _md.GetParameter(handle)))
                .ToList();
            var marked = allRows
                .Where(entry => _attrs.ExactBareMarkerCount(entry.Handle, carrier) > 0)
                .ToList();
            if (marked.Count == 0) continue;
            if (marked.Count != 1 || _attrs.ExactBareMarkerCount(marked[0].Handle, carrier) != 1
                || marked[0].Row.SequenceNumber != 1
                || _md.GetString(method.Name) is ".ctor" or ".cctor")
                throw new InvalidDataException(
                    $"[KotlinExtensionReceiver] must occur once on the leading semantic parameter of a method");
            var physicalCount = allRows.Count(entry => entry.Row.SequenceNumber > 0);
            if (SemanticParameterCount(methodHandle, physicalCount) == 0)
                throw new InvalidDataException(
                    "[KotlinExtensionReceiver] cannot mark a nullable witness or return parameter");
        }
    }

    private bool HasKotlinExtensionReceiver(MethodDefinitionHandle methodHandle)
    {
        var method = _md.GetMethodDefinition(methodHandle);
        var physical = PhysicalParameters(method);
        var semanticCount = SemanticParameterCount(methodHandle, physical.Count);
        if (semanticCount == 0) return false;
        return _attrs.ExactBareMarkerCount(physical[0].Handle,
            MetadataAttributes.DotKtNs + "KotlinExtensionReceiverAttribute") == 1;
    }

    private int SemanticParameterCount(MethodDefinitionHandle methodHandle, int physicalCount)
    {
        var identity = KotlinDeclarationIdentityCarrier(methodHandle);
        if (identity?.Parameters is not { } semanticParameters) return physicalCount;
        if (semanticParameters.Count + identity.NullableWitnessTypeParameterIndices.Count != physicalCount)
            throw new InvalidDataException(
                $"[KotlinDeclarationIdentity] signature parameter count does not match " +
                $"'{_md.GetString(_md.GetMethodDefinition(methodHandle).Name)}'");
        return semanticParameters.Count;
    }

    private Annotation ClrExternalAnnotation(NameTable names, TypeDefinitionHandle owner)
    {
        var annotation = new Annotation { Id = names.Class("kotlin.clr.ClrExternal") };
        annotation.Argument.Add(new Annotation.Types.Argument
        {
            NameId = names.String("owner"),
            Value = new Annotation.Types.Argument.Types.Value
            {
                Type = Annotation.Types.Argument.Types.Value.Types.Type.String,
                StringValue = names.String(_semanticOwnerNames.GetValueOrDefault(owner) ?? KotlinFullName(owner)),
            },
        });
        annotation.Argument.Add(new Annotation.Types.Argument
        {
            NameId = names.String("physicalOwner"),
            Value = new Annotation.Types.Argument.Types.Value
            {
                Type = Annotation.Types.Argument.Types.Value.Types.Type.String,
                StringValue = names.String(ExactMetadataTypeName(owner)),
            },
        });
        return annotation;
    }

    private void ApplyPInvokeProjection(MethodDefinition method, Function function, NameTable names)
    {
        var import = method.GetImport();
        var hasImportFlag = (method.Attributes & MethodAttributes.PinvokeImpl) != 0;
        var hasImportRow = !import.Module.IsNil;
        if (hasImportFlag != hasImportRow)
            throw new InvalidDataException(
                $"P/Invoke MethodDef '{_md.GetString(method.Name)}' has an inconsistent PinvokeImpl/ImplMap pair");
        if (!hasImportRow) return;

        function.Flags |= (1 << 0) | (1 << 12); // HAS_ANNOTATIONS + IS_EXTERNAL
        var annotation = new Annotation
        {
            Id = names.Class("System.Runtime.InteropServices.DllImportAttribute"),
        };
        AddStringAnnotationArgument(
            annotation,
            names,
            "dllName",
            _md.GetString(_md.GetModuleReference(import.Module).Name));

        var entryPoint = _md.GetString(import.Name);
        var methodName = _md.GetString(method.Name);
        if (entryPoint != methodName)
            AddStringAnnotationArgument(annotation, names, "EntryPoint", entryPoint);

        switch (import.Attributes & MethodImportAttributes.CallingConventionMask)
        {
            case MethodImportAttributes.None: break;
            case MethodImportAttributes.CallingConventionWinApi:
                AddEnumAnnotationArgument(annotation, names, "CallingConvention",
                    "System.Runtime.InteropServices.CallingConvention", "Winapi");
                break;
            case MethodImportAttributes.CallingConventionCDecl:
                AddEnumAnnotationArgument(annotation, names, "CallingConvention",
                    "System.Runtime.InteropServices.CallingConvention", "Cdecl");
                break;
            case MethodImportAttributes.CallingConventionStdCall:
                AddEnumAnnotationArgument(annotation, names, "CallingConvention",
                    "System.Runtime.InteropServices.CallingConvention", "StdCall");
                break;
            case MethodImportAttributes.CallingConventionThisCall:
                AddEnumAnnotationArgument(annotation, names, "CallingConvention",
                    "System.Runtime.InteropServices.CallingConvention", "ThisCall");
                break;
            case MethodImportAttributes.CallingConventionFastCall:
                AddEnumAnnotationArgument(annotation, names, "CallingConvention",
                    "System.Runtime.InteropServices.CallingConvention", "FastCall");
                break;
            default:
                throw new InvalidDataException(
                    $"P/Invoke MethodDef '{methodName}' has an unsupported calling convention");
        }

        switch (import.Attributes & MethodImportAttributes.CharSetMask)
        {
            case MethodImportAttributes.None: break;
            case MethodImportAttributes.CharSetAnsi:
                AddEnumAnnotationArgument(annotation, names, "CharSet",
                    "System.Runtime.InteropServices.CharSet", "Ansi");
                break;
            case MethodImportAttributes.CharSetUnicode:
                AddEnumAnnotationArgument(annotation, names, "CharSet",
                    "System.Runtime.InteropServices.CharSet", "Unicode");
                break;
            case MethodImportAttributes.CharSetAuto:
                AddEnumAnnotationArgument(annotation, names, "CharSet",
                    "System.Runtime.InteropServices.CharSet", "Auto");
                break;
            default:
                throw new InvalidDataException($"P/Invoke MethodDef '{methodName}' has an unsupported character set");
        }

        if ((import.Attributes & MethodImportAttributes.ExactSpelling) != 0)
            AddBooleanAnnotationArgument(annotation, names, "ExactSpelling", true);
        if ((import.Attributes & MethodImportAttributes.SetLastError) != 0)
            AddBooleanAnnotationArgument(annotation, names, "SetLastError", true);
        if ((method.ImplAttributes & MethodImplAttributes.PreserveSig) == 0)
            AddBooleanAnnotationArgument(annotation, names, "PreserveSig", false);

        AddTriStateAnnotationArgument(
            annotation,
            names,
            "BestFitMapping",
            import.Attributes & MethodImportAttributes.BestFitMappingMask,
            MethodImportAttributes.BestFitMappingEnable,
            MethodImportAttributes.BestFitMappingDisable,
            methodName);
        AddTriStateAnnotationArgument(
            annotation,
            names,
            "ThrowOnUnmappableChar",
            import.Attributes & MethodImportAttributes.ThrowOnUnmappableCharMask,
            MethodImportAttributes.ThrowOnUnmappableCharEnable,
            MethodImportAttributes.ThrowOnUnmappableCharDisable,
            methodName);
        function.FunctionAnnotation.Add(annotation);
    }

    private static void AddTriStateAnnotationArgument(
        Annotation annotation,
        NameTable names,
        string name,
        MethodImportAttributes value,
        MethodImportAttributes enabled,
        MethodImportAttributes disabled,
        string methodName)
    {
        if (value == MethodImportAttributes.None) return;
        if (value == enabled) AddBooleanAnnotationArgument(annotation, names, name, true);
        else if (value == disabled) AddBooleanAnnotationArgument(annotation, names, name, false);
        else throw new InvalidDataException($"P/Invoke MethodDef '{methodName}' has an invalid {name} flag pair");
    }

    private static void AddStringAnnotationArgument(
        Annotation annotation, NameTable names, string name, string value) =>
        annotation.Argument.Add(new Annotation.Types.Argument
        {
            NameId = names.String(name),
            Value = new Annotation.Types.Argument.Types.Value
            {
                Type = Annotation.Types.Argument.Types.Value.Types.Type.String,
                StringValue = names.String(value),
            },
        });

    private static void AddBooleanAnnotationArgument(
        Annotation annotation, NameTable names, string name, bool value) =>
        annotation.Argument.Add(new Annotation.Types.Argument
        {
            NameId = names.String(name),
            Value = new Annotation.Types.Argument.Types.Value
            {
                Type = Annotation.Types.Argument.Types.Value.Types.Type.Boolean,
                IntValue = value ? 1 : 0,
            },
        });

    private static void AddEnumAnnotationArgument(
        Annotation annotation, NameTable names, string name, string enumClass, string entry) =>
        annotation.Argument.Add(new Annotation.Types.Argument
        {
            NameId = names.String(name),
            Value = new Annotation.Types.Argument.Types.Value
            {
                Type = Annotation.Types.Argument.Types.Value.Types.Type.Enum,
                ClassId = names.Class(enumClass),
                EnumValueId = names.String(entry),
            },
        });

    private static Annotation ClrAttributeNamedArgumentAnnotation(
        NameTable names, string kind, string name)
    {
        var annotation = new Annotation { Id = names.Class("kotlin.clr.ClrAttributeNamedArgument") };
        annotation.Argument.Add(new Annotation.Types.Argument
        {
            NameId = names.String("kind"),
            Value = new Annotation.Types.Argument.Types.Value
            {
                Type = Annotation.Types.Argument.Types.Value.Types.Type.String,
                StringValue = names.String(kind),
            },
        });
        annotation.Argument.Add(new Annotation.Types.Argument
        {
            NameId = names.String("name"),
            Value = new Annotation.Types.Argument.Types.Value
            {
                Type = Annotation.Types.Argument.Types.Value.Types.Type.String,
                StringValue = names.String(name),
            },
        });
        return annotation;
    }

    private AttributeNamedArgument[] ProjectAttributeNamedArguments(
        TypeDefinitionHandle owner,
        TypeDefinition definition,
        NameTable names,
        SignatureDecoder signatures,
        GenericContext context)
    {
        var result = new List<AttributeNamedArgument>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fieldHandle in definition.GetFields())
        {
            var field = _md.GetFieldDefinition(fieldHandle);
            if ((field.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public ||
                (field.Attributes & (FieldAttributes.Static | FieldAttributes.InitOnly | FieldAttributes.Literal)) != 0)
                continue;
            var name = _md.GetString(field.Name);
            if (!seen.Add(name))
                throw new InvalidDataException(
                    $"CLR attribute '{MetadataTypeName(owner)}' declares duplicate named argument '{name}'");
            var type = ProjectType(
                fieldHandle,
                field.DecodeSignature(signatures, context),
                owner,
                names,
                signatures,
                context);
            result.Add(new AttributeNamedArgument("field", name, type));
        }

        foreach (var propertyHandle in definition.GetProperties())
        {
            var property = _md.GetPropertyDefinition(propertyHandle);
            var accessors = property.GetAccessors();
            if (accessors.Setter.IsNil) continue;
            var setter = _md.GetMethodDefinition(accessors.Setter);
            if ((setter.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public ||
                (setter.Attributes & MethodAttributes.Static) != 0)
                continue;
            var signature = property.DecodeSignature(signatures, context);
            if (signature.ParameterTypes.Length != 0) continue;
            var name = _md.GetString(property.Name);
            if (!seen.Add(name))
                throw new InvalidDataException(
                    $"CLR attribute '{MetadataTypeName(owner)}' declares duplicate named argument '{name}'");
            var valueParameter = setter.GetParameters()
                .Select(parameterHandle => (Handle: parameterHandle, Row: _md.GetParameter(parameterHandle)))
                .Single(parameter => parameter.Row.SequenceNumber == 1).Handle;
            var type = ProjectType(
                valueParameter,
                signature.ReturnType,
                owner,
                names,
                signatures,
                context);
            result.Add(new AttributeNamedArgument("property", name, type));
        }
        return result.ToArray();
    }

    private static Annotation KotlinDeclarationIdentityAnnotation(
        NameTable names, string id, string setterId)
    {
        var annotation = new Annotation { Id = names.Class("kotlin.clr.KotlinDeclarationIdentity") };
        annotation.Argument.Add(new Annotation.Types.Argument
        {
            NameId = names.String("id"),
            Value = new Annotation.Types.Argument.Types.Value
            {
                Type = Annotation.Types.Argument.Types.Value.Types.Type.String,
                StringValue = names.String(id),
            },
        });
        annotation.Argument.Add(new Annotation.Types.Argument
        {
            NameId = names.String("setterId"),
            Value = new Annotation.Types.Argument.Types.Value
            {
                Type = Annotation.Types.Argument.Types.Value.Types.Type.String,
                StringValue = names.String(setterId),
            },
        });
        return annotation;
    }

    private static Annotation ClrFlagsOperationAnnotation(NameTable names, string role)
    {
        var annotation = new Annotation { Id = names.Class("kotlin.clr.ClrFlagsOperation") };
        annotation.Argument.Add(new Annotation.Types.Argument
        {
            NameId = names.String("role"),
            Value = new Annotation.Types.Argument.Types.Value
            {
                Type = Annotation.Types.Argument.Types.Value.Types.Type.String,
                StringValue = names.String(role),
            },
        });
        return annotation;
    }

    private static IEnumerable<Annotation> ExplicitSlotAnnotations(NameTable names)
    {
        var deprecated = new Annotation { Id = names.Class("kotlin.Deprecated") };
        deprecated.Argument.Add(new Annotation.Types.Argument
        {
            NameId = names.String("message"),
            Value = new Annotation.Types.Argument.Types.Value
            {
                Type = Annotation.Types.Argument.Types.Value.Types.Type.String,
                StringValue = names.String("compiler-projected explicit interface slot"),
            },
        });
        deprecated.Argument.Add(new Annotation.Types.Argument
        {
            NameId = names.String("level"),
            Value = new Annotation.Types.Argument.Types.Value
            {
                Type = Annotation.Types.Argument.Types.Value.Types.Type.Enum,
                ClassId = names.Class("kotlin.DeprecationLevel"),
                EnumValueId = names.String("HIDDEN"),
            },
        });
        yield return deprecated;
    }

    private static Annotation ClrFieldAnnotation(NameTable names) =>
        new() { Id = names.Class("kotlin.clr.ClrField") };

    private static Annotation ClrLateinitFieldAnnotation(NameTable names) =>
        new() { Id = names.Class("kotlin.clr.ClrLateinitField") };

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
                owner.Function.Add(new Function
                {
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
            owner.Function.Add(new Function
            {
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
        if (suspend)
        {
            var logical = _attrs.CarrierType(
                methodHandle, MetadataAttributes.DotKtNs + "KotlinSuspendResultAttribute")
                ?? throw new InvalidDataException(
                    $"suspend MethodDef '{_md.GetString(method.Name)}' has no trusted logical-result carrier");
            return signatures.FromTypeNode(logical);
        }
        return ProjectType(
            returnHandle,
            physical,
            methodHandle,
            names,
            signatures,
            context,
            flowContract: true);
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
    // unlike its supertype list — and restored as the complete source list. The CLR rows are an erased physical
    // approximation of that same list, so retaining any of them beside the carrier can publish a false stronger bound.
    private void RestoreErasedSupertypes(TypeDefinitionHandle handle, Class result, SignatureDecoder signatures,
        NameTable names, int capturedOuterTypeParameterCount)
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
        RestoreErasedBounds(doc, result, signatures, capturedOuterTypeParameterCount);
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
        SignatureDecoder signatures, int capturedOuterTypeParameterCount)
    {
        if (!doc.RootElement.TryGetProperty("bounds", out var bounds)) return;
        if (bounds.ValueKind != System.Text.Json.JsonValueKind.Object || !bounds.EnumerateObject().Any())
            throw new InvalidDataException("malformed [KotlinSupertypes] bounds");
        var seen = new HashSet<int>();
        foreach (var entry in bounds.EnumerateObject())
        {
            if (!int.TryParse(entry.Name, out var index) || index < 0 || !seen.Add(index)
                || entry.Value.ValueKind != System.Text.Json.JsonValueKind.Array)
                throw new InvalidDataException("malformed [KotlinSupertypes] bound entry");
            var restoredNodes = entry.Value.EnumerateArray().Select(TypeNode.Read).ToArray();
            if (restoredNodes.Length == 0)
                throw new InvalidDataException("empty [KotlinSupertypes] constraint list");
            // A CLR nested TypeDef flattens its enclosing type parameters before its own. Kotlin metadata instead
            // owns those declarations on the enclosing class and omits the captured prefix from the inner class.
            // Their constraints therefore have no declaration to restore here; only the inner class's retained slots
            // are indexed in `result.TypeParameter`.
            if (index < capturedOuterTypeParameterCount) continue;
            var parameter = result.TypeParameter.FirstOrDefault(p => p.Id == index)
                ?? throw new InvalidDataException("[KotlinSupertypes] bound index exceeds type generic arity");
            var restored = restoredNodes.Select(signatures.FromTypeNode).ToArray();
            // NullableGenericErasure records the parameter's WHOLE source constraint list when any constraint moves.
            // The CLR rows are only its physical approximation: merging leaves an erased `object` row beside a
            // restored `T?` and publishes the stronger, false Kotlin bound `E : Any, T?`. Replace the parameter as
            // one semantic unit so the KLIB surface is exactly the producer-authored list.
            parameter.UpperBound.Clear();
            parameter.UpperBound.Add(restored);
        }
    }

    private KType ProjectType(
        EntityHandle slot,
        KType physical,
        EntityHandle contextOwner,
        NameTable names,
        SignatureDecoder signatures,
        GenericContext context,
        bool flowContract = false)
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
        var contextByte = NullableContext(contextOwner);
        // DotKt signatures are non-null by Kotlin default. Unlike Roslyn,
        // ilemit need not emit a NullableContext(1) row for every declaration;
        // absence therefore means non-null for a trusted DotKt assembly, while
        // it remains oblivious/platform for an ordinary CLR assembly.
        if (_attrs.IsDotKtAssembly && contextByte == 0)
            contextByte = 1;
        result = carrierName switch
        {
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
    private static TypeNode StripOuterNullability(TypeNode type, SignatureDecoder signatures) => type switch
    {
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
            current = current.Kind switch
            {
                HandleKind.MethodDefinition => _md.GetMethodDefinition((MethodDefinitionHandle)current).GetDeclaringType(),
                HandleKind.TypeDefinition => _md.GetTypeDefinition((TypeDefinitionHandle)current).GetDeclaringType(),
                _ => default,
            };
        }
        return 0;
    }

    private void ValidateCompanionExtensionPhysicalMembers(TypeDefinitionHandle handle, TypeDefinition def)
    {
        const string attribute = MetadataAttributes.DotKtNs + "KotlinCompanionExtensionAttribute";
        var isFileFacade = _attrs.Has(handle, MetadataAttributes.DotKtNs + "KotlinFileClassAttribute");
        foreach (var methodHandle in def.GetMethods())
        {
            using var carrier = _attrs.CarrierDocument(methodHandle, attribute);
            if (carrier is null) continue;
            var payload = ReadCompanionExtensionPayload(carrier);
            var method = _md.GetMethodDefinition(methodHandle);
            if (!isFileFacade ||
                (method.Attributes & MethodAttributes.Static) == 0 ||
                payload.Kind is not ("function" or "get" or "set"))
                throw new InvalidDataException(
                    "[KotlinCompanionExtension] must annotate a static method on a Kotlin file facade");
            if ((method.Attributes & MethodAttributes.SpecialName) != 0)
                throw new InvalidDataException(
                    "[KotlinCompanionExtension] method carrier must annotate an ordinary static method");
        }
        foreach (var fieldHandle in def.GetFields())
        {
            using var carrier = _attrs.CarrierDocument(fieldHandle, attribute);
            if (carrier is null) continue;
            var payload = ReadCompanionExtensionPayload(carrier);
            if (!isFileFacade ||
                (_md.GetFieldDefinition(fieldHandle).Attributes & FieldAttributes.Static) == 0 ||
                payload.Kind != "field")
                throw new InvalidDataException(
                    "[KotlinCompanionExtension] must annotate a static field on a Kotlin file facade");
        }
    }

    /// The Kotlin type a declaration is associated with when it is a Kotlin 2.4 COMPANION EXTENSION
    /// (`companion fun C.foo()`, `companion val C.bar`), or null when it is not one.
    ///
    /// The frontend drops a companion extension's receiver parameter, so the emitted member carries no physical trace
    /// of the association and it is read back from the trusted [KotlinCompanionExtension] carrier instead. No name,
    /// library or physical-layout inference participates: an assembly without the carrier simply has no such
    /// declaration.
    private sealed record CompanionExtensionPayload(TypeNode.Fqn Receiver, string Name, string Kind);

    private static CompanionExtensionPayload ReadCompanionExtensionPayload(
        System.Text.Json.JsonDocument document)
    {
        var root = document.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object
            || root.EnumerateObject().Count() != 3
            || !root.TryGetProperty("receiver", out var receiver)
            || !root.TryGetProperty("name", out var name)
            || name.ValueKind != System.Text.Json.JsonValueKind.String
            || string.IsNullOrEmpty(name.GetString())
            || !root.TryGetProperty("kind", out var kind)
            || kind.ValueKind != System.Text.Json.JsonValueKind.String
            || TypeNode.Read(receiver) is not TypeNode.Fqn { Args: null } receiverType)
            throw new InvalidDataException("malformed [KotlinCompanionExtension] payload");
        return new CompanionExtensionPayload(receiverType, name.GetString()!, kind.GetString()!);
    }

    private sealed record CompanionExtensionInfo(KType Receiver, string Name);

    private CompanionExtensionInfo? CompanionExtension(
        EntityHandle slot, SignatureDecoder signatures, string expectedKind)
    {
        using var document = _attrs.CarrierDocument(
            slot, MetadataAttributes.DotKtNs + "KotlinCompanionExtensionAttribute");
        if (document is null) return null;
        var payload = ReadCompanionExtensionPayload(document);
        if (payload.Kind != expectedKind)
            throw new InvalidDataException("malformed [KotlinCompanionExtension] payload");
        return new CompanionExtensionInfo(
            signatures.FromTypeNode(payload.Receiver),
            payload.Name);
    }

    private void PromoteReceiver(
        MethodDefinitionHandle handle,
        MethodDefinition method,
        Function function,
        bool recognizeClrExtension = true)
    {
        if (function.ValueParameter.Count == 0) return;
        var isReceiver =
            (recognizeClrExtension &&
                _attrs.Has(handle, "System.Runtime.CompilerServices.ExtensionAttribute", requireTrust: false)) ||
            HasKotlinExtensionReceiver(handle);
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
        GenericContext context,
        IReadOnlyList<TypeNode>? semanticTypes = null,
        int hiddenTrailingParameters = 0)
    {
        if (semanticTypes is not null && semanticTypes.Count + hiddenTrailingParameters != types.Length)
            throw new InvalidDataException(
                $"[KotlinDeclarationIdentity] signature parameter count does not match '{_md.GetString(method.Name)}'");
        var rows = method.GetParameters().Select(h => (Handle: h, Row: _md.GetParameter(h)))
            .Where(p => p.Row.SequenceNumber > 0).ToDictionary(p => p.Row.SequenceNumber);
        var visibleCount = semanticTypes?.Count ?? types.Length;
        for (var i = 0; i < visibleCount; i++)
        {
            if (!rows.TryGetValue(i + 1, out var entry))
            {
                // ECMA-335 permits a signature parameter without a Param row.
                // Synthesized delegates emitted by ilemit use that compact
                // form. The signature is authoritative; only the optional
                // name/attributes are absent.
                yield return new ValueParameter
                {
                    Name = names.String($"arg{i}"),
                    Type = semanticTypes is null
                        ? ProjectType(default(EntityHandle), types[i], methodHandle, names, signatures, context)
                        : signatures.FromTypeNode(semanticTypes[i]),
                };
                continue;
            }
            var row = entry.Row;
            var name = row.Name.IsNil ? $"arg{i}" : _md.GetString(row.Name);
            var projected = semanticTypes is null
                ? ProjectType(entry.Handle, types[i], methodHandle, names, signatures, context)
                : signatures.FromTypeNode(semanticTypes[i]);
            var flags = (row.Attributes & (ParameterAttributes.Optional | ParameterAttributes.HasDefault)) != 0 ||
                _attrs.Has(entry.Handle, "kotlin.clr.KotlinDefault", requireTrust: false) ? 1 << 1 : 0;
            var value = new ValueParameter
            {
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

    // The exact ECMA-335 TypeDef identity. Unlike MetadataTypeName, this retains each nested segment's own `N arity;
    // a flattened Kotlin argument vector cannot distinguish Outer`1+Leaf`1 from Outer+Leaf`2 by itself.
    private string ExactMetadataTypeName(TypeDefinitionHandle handle)
    {
        var chain = new Stack<string>();
        var current = handle;
        string package = "";
        while (!current.IsNil)
        {
            var def = _md.GetTypeDefinition(current);
            chain.Push(_md.GetString(def.Name));
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
        return handle.Kind switch
        {
            HandleKind.TypeReference => IsReference(_md.GetTypeReference((TypeReferenceHandle)handle)),
            HandleKind.TypeDefinition => IsDefinition(_md.GetTypeDefinition((TypeDefinitionHandle)handle)),
            _ => false,
        };
        bool IsReference(TypeReference t) => _md.GetString(t.Namespace) == ns && _md.GetString(t.Name) == name;
        bool IsDefinition(TypeDefinition t) => _md.GetString(t.Namespace) == ns && _md.GetString(t.Name) == name;
    }

    private bool IsExactSystemFlagsEnum(TypeDefinitionHandle handle, TypeDefinition definition)
    {
        if (!_publicTypeCatalog.TryResolveDefinition(
                _md, definition.BaseType, out var enumBase, _definitionPath) ||
            !IsTargetCoreLibraryEnum(enumBase) ||
            !HasSupportedEnumStorage(definition))
            return false;
        var flags = _attrs.AttributeTypes(handle, "System.FlagsAttribute");
        if (flags.Count == 0) return false;
        if (flags.Count != 1)
            throw new InvalidDataException(
                $"enum '{MetadataTypeName(handle)}' carries duplicate System.FlagsAttribute applications");
        if (!_publicTypeCatalog.TryResolveDefinition(_md, flags[0], out var flagsType, _definitionPath) ||
            !IsResolvedSystemType(flagsType, "FlagsAttribute"))
            return false;
        return StringComparer.Ordinal.Equals(enumBase.DefinitionPath, flagsType.DefinitionPath);

        bool IsTargetCoreLibraryEnum(ResolvedTypeDefinition resolvedEnum)
        {
            if (!IsResolvedSystemType(resolvedEnum, "Enum")) return false;
            var enumDefinition = resolvedEnum.Reader.GetTypeDefinition(resolvedEnum.Handle);
            if (!_publicTypeCatalog.TryResolveDefinition(
                    resolvedEnum.Reader, enumDefinition.BaseType, out var valueType, resolvedEnum.DefinitionPath) ||
                !IsResolvedSystemType(valueType, "ValueType") ||
                !StringComparer.Ordinal.Equals(resolvedEnum.DefinitionPath, valueType.DefinitionPath))
                return false;
            var valueDefinition = valueType.Reader.GetTypeDefinition(valueType.Handle);
            if (!_publicTypeCatalog.TryResolveDefinition(
                    valueType.Reader, valueDefinition.BaseType, out var objectType, valueType.DefinitionPath) ||
                !IsResolvedSystemType(objectType, "Object") ||
                !StringComparer.Ordinal.Equals(resolvedEnum.DefinitionPath, objectType.DefinitionPath))
                return false;
            return objectType.Reader.GetTypeDefinition(objectType.Handle).BaseType.IsNil;
        }

        bool HasSupportedEnumStorage(TypeDefinition enumDefinition)
        {
            var storage = enumDefinition.GetFields()
                .Select(fieldHandle => _md.GetFieldDefinition(fieldHandle))
                .Where(field => (field.Attributes & FieldAttributes.Static) == 0)
                .ToArray();
            if (storage.Length != 1 || _md.GetString(storage[0].Name) != "value__") return false;
            var signature = _md.GetBlobBytes(storage[0].Signature);
            return signature.Length == 2 && signature[0] == 0x06 && signature[1] is >= 0x04 and <= 0x0b;
        }

        static bool IsResolvedSystemType(ResolvedTypeDefinition resolved, string name)
        {
            var type = resolved.Reader.GetTypeDefinition(resolved.Handle);
            return resolved.Reader.GetString(type.Namespace) == "System" &&
                resolved.Reader.GetString(type.Name) == name;
        }
    }

    private void AddFlagsEnumOperations(
        TypeDefinitionHandle handle,
        Class result,
        NameTable names)
    {
        var self = new KType { ClassName = result.FqName };
        foreach (var parameter in result.TypeParameter)
            self.Argument.Add(new KType.Types.Argument
            {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = new KType { TypeParameter = parameter.Id },
            });

        void Add(string name, string role, int kotlinFlags, bool takesArgument, bool returnsBoolean)
        {
            var nameId = names.String(name);
            var parameters = takesArgument
                ? new[]
                {
                    new ValueParameter
                    {
                        Name = names.String(role == "contains" ? "flag" : "other"),
                        Type = self.Clone(),
                    },
                }
                : Array.Empty<ValueParameter>();
            var collision = result.Function.FirstOrDefault(function =>
                function.Name == nameId &&
                function.TypeParameter.Count == 0 &&
                function.ValueParameter.Count == parameters.Length &&
                function.ValueParameter.Select(parameter => parameter.Type)
                    .SequenceEqual(parameters.Select(parameter => parameter.Type)));
            if (collision is not null)
                throw new InvalidDataException(
                    $"CLR [Flags] enum projection '{MetadataTypeName(handle)}' cannot synthesize '{name}': " +
                    "an actual CLR member has the same complete Kotlin declaration signature");

            var function = new Function
            {
                Name = nameId,
                Flags = Flags.Callable(MethodAttributes.Public, modality: 0, kotlinFlags: kotlinFlags),
                ReturnType = returnsBoolean
                    ? new KType { ClassName = names.Class("kotlin.Boolean") }
                    : self.Clone(),
            };
            function.ValueParameter.Add(parameters);
            function.FunctionAnnotation.Add(ClrFlagsOperationAnnotation(names, role));
            result.Function.Add(function);
        }

        Add("or", "or", kotlinFlags: 1, takesArgument: true, returnsBoolean: false);
        Add("and", "and", kotlinFlags: 1, takesArgument: true, returnsBoolean: false);
        Add("xor", "xor", kotlinFlags: 1, takesArgument: true, returnsBoolean: false);
        Add("inv", "inv", kotlinFlags: 0, takesArgument: false, returnsBoolean: false);
        Add("contains", "contains", kotlinFlags: 2, takesArgument: true, returnsBoolean: true);
    }

    // System.ValueType and System.Enum are physical CLR roots, not classifiers in Kotlin's nominal subtype lattice.
    // csc emits them as rows alongside struct/enum constraints; exposing the rows as KLIB upper bounds makes every
    // otherwise-legal Kotlin value fail frontend checking. bir2cir validates these rows together with the associated
    // GenericParameterAttributes against the actual physical TypeSpec.
    private static bool IsClrPhysicalOnlyConstraint(MetadataReader reader, EntityHandle handle)
    {
        if (handle.IsNil) return false;
        return handle.Kind switch
        {
            HandleKind.TypeReference => MatchReference(reader.GetTypeReference((TypeReferenceHandle)handle)),
            HandleKind.TypeDefinition => MatchDefinition(reader.GetTypeDefinition((TypeDefinitionHandle)handle)),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle)
                .DecodeSignature(ClrPhysicalConstraintTypeProvider.Instance, genericContext: null),
            _ => false,
        };
        bool MatchReference(TypeReference type) => reader.GetString(type.Namespace) == "System" &&
            reader.GetString(type.Name) is "ValueType" or "Enum";
        bool MatchDefinition(TypeDefinition type) => reader.GetString(type.Namespace) == "System" &&
            reader.GetString(type.Name) is "ValueType" or "Enum";
    }

    // One projection policy for class parameters and every callable/accessor path: only Kotlin-nominal rows enter
    // KLIB bounds. CLR-only rows stay authoritative in the DLL and are validated by bir2cir at constructed uses.
    private static IEnumerable<GenericParameterConstraint> KotlinNominalConstraints(
        MetadataReader reader, GenericParameter parameter)
    {
        foreach (var handle in parameter.GetConstraints())
        {
            var constraint = reader.GetGenericParameterConstraint(handle);
            if (!IsClrPhysicalOnlyConstraint(reader, constraint.Type))
                yield return constraint;
        }
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
    public const int DeclarationMember = 0;
    public const int FakeOverride = 1;

    // metadata.proto: hasAnnotations(1), visibility(3), modality(2), then class kind/member kind.
    public static int Declaration(int modality, int kind, bool isValue = false, bool isFun = false,
        bool hasEnumEntries = false, bool isInner = false) =>
        6 | (modality << 4) | (kind << 6)
        | (isInner ? 1 << 9 : 0) | (isValue ? 1 << 13 : 0) | (isFun ? 1 << 14 : 0)
        | (hasEnumEntries ? 1 << 15 : 0);
    public static int Callable(MethodAttributes attrs, int modality, int kotlinFlags = 0, bool isInline = false,
        int memberKind = DeclarationMember) =>
        Visibility(attrs) | (modality << 4)
        | (memberKind << 6)
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
    public static int AsOpen(int flags) => (flags & ~(3 << 4)) | (1 << 4);

    public static bool IsStaticFunction(int flags) => (flags & (1 << 18)) != 0;
    public static int Property(MethodAttributes attrs, bool canWrite, bool isStatic,
        int memberKind = DeclarationMember) =>
        Visibility(attrs) | (((attrs & MethodAttributes.Abstract) != 0 ? 2
            : (attrs & MethodAttributes.Virtual) != 0 && (attrs & MethodAttributes.Final) == 0 ? 1 : 0) << 4)
        | (memberKind << 6)
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
        QualifiedNames.QualifiedName.Add(new QualifiedNameTable.Types.QualifiedName
        {
            ParentQualifiedName = parent,
            ShortName = shortName,
            Kind = kind,
        });
        _qualified.Add(key, id);
        return id;
    }
}

// A generic-constraint row may wrap its physical root in custom modifiers (`unmanaged` is encoded as
// `System.ValueType modreq(IsUnmanagedAttribute)`). Decode only enough of a TypeSpec to identify that root; every
// composite form remains nominal, and a modifier preserves the answer of the type it annotates.
internal sealed class ClrPhysicalConstraintTypeProvider : ISignatureTypeProvider<bool, object?>
{
    public static ClrPhysicalConstraintTypeProvider Instance { get; } = new();

    public bool GetArrayType(bool elementType, ArrayShape shape) => false;
    public bool GetByReferenceType(bool elementType) => false;
    public bool GetFunctionPointerType(MethodSignature<bool> signature) => false;
    public bool GetGenericInstantiation(bool genericType, ImmutableArray<bool> typeArguments) => false;
    public bool GetGenericMethodParameter(object? genericContext, int index) => false;
    public bool GetGenericTypeParameter(object? genericContext, int index) => false;
    public bool GetModifiedType(bool modifier, bool unmodifiedType, bool isRequired) => unmodifiedType;
    public bool GetPinnedType(bool elementType) => false;
    public bool GetPointerType(bool elementType) => false;
    public bool GetPrimitiveType(PrimitiveTypeCode typeCode) => false;
    public bool GetSZArrayType(bool elementType) => false;
    public bool GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        Match(reader.GetString(reader.GetTypeDefinition(handle).Namespace),
            reader.GetString(reader.GetTypeDefinition(handle).Name));
    public bool GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        Match(reader.GetString(reader.GetTypeReference(handle).Namespace),
            reader.GetString(reader.GetTypeReference(handle).Name));
    public bool GetTypeFromSpecification(MetadataReader reader, object? genericContext,
        TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    static bool Match(string ns, string name) => ns == "System" && name is "ValueType" or "Enum";
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
        var scope = reference.ResolutionScope.Kind switch
        {
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

// SignatureDecoder is package-local because every KLIB package owns a distinct NameTable and mutable observations
// made while decoding that package. The facts discoverable solely from this assembly's TypeDef table are different:
// they are immutable assembly input. Discover them once and give each decoder its own mutable value-type overlay.
internal sealed record SignatureDecoderSeeds(
    ImmutableDictionary<string, TypeDefinitionHandle> DelegateDefinitions,
    ImmutableHashSet<string> ValueTypeNames)
{
    // The Kotlin primitives, plus `kotlin.Unit`: Unit is physically a CLR class, but it is also the name ECMA `void`
    // decodes to, so it holds no NRT byte and takes no annotation. bir2cir's [NullableFlags] writer implements the
    // same rule from the other side. This intentional DotKt deviation is recorded in docs/dotkt-semantics.md § 9.
    private static readonly string[] BuiltInNoNrtTypeNames =
    [
        "kotlin.Unit", "kotlin.Boolean", "kotlin.Char", "kotlin.Byte", "kotlin.UByte",
        "kotlin.Short", "kotlin.UShort", "kotlin.Int", "kotlin.UInt",
        "kotlin.Long", "kotlin.ULong", "kotlin.Float", "kotlin.Double",
    ];

    internal static SignatureDecoderSeeds Discover(MetadataReader md, ArityNames arityNames)
    {
        var delegates = ImmutableDictionary.CreateBuilder<string, TypeDefinitionHandle>(StringComparer.Ordinal);
        var valueTypes = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        valueTypes.UnionWith(BuiltInNoNrtTypeNames);
        foreach (var handle in md.TypeDefinitions)
        {
            var definition = md.GetTypeDefinition(handle);
            if (IsSystemType(md, definition.BaseType, "System", "MulticastDelegate"))
                delegates[DefinitionKotlinName(md, arityNames, handle)] = handle;
            else if (IsSystemType(md, definition.BaseType, "System", "ValueType") ||
                     IsSystemType(md, definition.BaseType, "System", "Enum"))
                valueTypes.Add(arityNames.Full(
                    md.GetString(definition.Namespace), md.GetString(definition.Name)));
        }
        return new SignatureDecoderSeeds(delegates.ToImmutable(), valueTypes.ToImmutable());
    }

    internal static (string Package, IReadOnlyList<string> Names) DefinitionKotlinPath(
        MetadataReader reader,
        ArityNames arityNames,
        TypeDefinitionHandle handle)
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
            names.Add(arityNames.Simple(rawScope, metadataName));
            rawScope = string.IsNullOrEmpty(rawScope) ? metadataName : rawScope + "." + metadataName;
        }
        return (package, names);
    }

    internal static string DefinitionKotlinName(
        MetadataReader reader,
        ArityNames arityNames,
        TypeDefinitionHandle handle)
    {
        var (package, names) = DefinitionKotlinPath(reader, arityNames, handle);
        return string.Join(".", names.Prepend(package).Where(x => !string.IsNullOrEmpty(x)));
    }

    private static bool IsSystemType(MetadataReader md, EntityHandle handle, string ns, string name)
    {
        if (handle.IsNil) return false;
        return handle.Kind switch
        {
            HandleKind.TypeReference => MatchReference(md.GetTypeReference((TypeReferenceHandle)handle)),
            HandleKind.TypeDefinition => MatchDefinition(md.GetTypeDefinition((TypeDefinitionHandle)handle)),
            _ => false,
        };
        bool MatchReference(TypeReference type) =>
            md.GetString(type.Namespace) == ns && md.GetString(type.Name) == name;
        bool MatchDefinition(TypeDefinition type) =>
            md.GetString(type.Namespace) == ns && md.GetString(type.Name) == name;
    }
}

// An AssemblyScanner projects every visible namespace with a distinct NameTable, but all of those package-local
// decoders read the same referenced assemblies. Keep each referenced PE and its read-only metadata prerequisites open
// for the scanner's lifetime so the first external delegate in every namespace does not reopen and rescan it.
internal sealed class ExternalSignatureDecoderCache : IDisposable
{
    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);
    private readonly string? _inheritedArityClashes;

    internal ExternalSignatureDecoderCache(string? inheritedArityClashes) =>
        _inheritedArityClashes = inheritedArityClashes;

    internal Source Get(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!_sources.TryGetValue(fullPath, out var source))
        {
            source = new Source(fullPath, _inheritedArityClashes);
            _sources.Add(fullPath, source);
        }
        return source;
    }

    internal Source Get(string path, MetadataReader reader)
    {
        var fullPath = Path.GetFullPath(path);
        if (!_sources.TryGetValue(fullPath, out var source))
        {
            // PublicTypeCatalog owns this reader until after AssemblyScanner is disposed. Borrow it rather than
            // opening the same PE a second time; a direct delegate lookup may instead have populated an owned source.
            source = new Source(reader, _inheritedArityClashes);
            _sources.Add(fullPath, source);
        }
        return source;
    }

    public void Dispose()
    {
        foreach (var source in _sources.Values) source.Dispose();
        _sources.Clear();
    }

    internal sealed class Source : IDisposable
    {
        private readonly FileStream? _file;
        private readonly PEReader? _pe;

        internal Source(MetadataReader reader, string? inheritedArityClashes)
        {
            Reader = reader;
            ArityNames = ArityNames.Create(Reader, inheritedArityClashes);
            Attributes = new MetadataAttributes(Reader);
            Seeds = SignatureDecoderSeeds.Discover(Reader, ArityNames);
        }

        internal Source(string path, string? inheritedArityClashes)
        {
            _file = File.OpenRead(path);
            PEReader? pe = null;
            try
            {
                pe = new PEReader(_file, PEStreamOptions.PrefetchMetadata);
                _pe = pe;
                if (!pe.HasMetadata)
                    throw new InvalidDataException(
                        $"delegate catalog target is not a managed PE: {path}");
                Reader = pe.GetMetadataReader();
                ArityNames = ArityNames.Create(Reader, inheritedArityClashes);
                Attributes = new MetadataAttributes(Reader);
                Seeds = SignatureDecoderSeeds.Discover(Reader, ArityNames);
            }
            catch
            {
                pe?.Dispose();
                _file.Dispose();
                throw;
            }
        }

        internal MetadataReader Reader { get; }
        internal MetadataAttributes Attributes { get; }
        internal ArityNames ArityNames { get; }
        internal SignatureDecoderSeeds Seeds { get; }

        public void Dispose()
        {
            _pe?.Dispose();
            _file?.Dispose();
        }
    }
}

// CLR delegate signatures may form recursive graphs, but Kotlin metadata has no recursive function-type constructor:
// expanding one edge to `Any?` would make the public type depend on which delegate happened to be visited first.
// Track the exact resolved definitions on the active expansion path and reject only the recursive graph. The catalog's
// definition path + TypeDef row remains the same when a cross-assembly edge reopens an assembly, and unlike MVID it is
// an identity assigned by this exact resolved-input universe rather than a producer-supplied uniqueness hint.
internal sealed class DelegateDecodingContext
{
    private readonly List<Entry> _active = [];

    internal IDisposable Enter(
        string definitionPath,
        MetadataReader reader,
        ArityNames arityNames,
        TypeDefinitionHandle handle)
    {
        var key = new Key(definitionPath, MetadataTokens.GetRowNumber(handle));
        var cycleStart = _active.FindIndex(entry => entry.Key == key);
        if (cycleStart >= 0)
        {
            var repeated = new Entry(key, reader, arityNames, handle);
            var cycle = _active.Skip(cycleStart).Append(repeated).Select(DisplayName);
            throw new InvalidDataException(
                "recursive CLR delegate graph cannot be represented as a finite Kotlin function type: " +
                string.Join(" -> ", cycle));
        }

        _active.Add(new Entry(key, reader, arityNames, handle));
        return new ExitScope(this, key);
    }

    private static string DisplayName(Entry entry)
    {
        var reader = entry.Reader;
        var assemblyName = reader.IsAssembly
            ? reader.GetString(reader.GetAssemblyDefinition().Name)
            : reader.GetString(reader.GetModuleDefinition().Name);
        return assemblyName + "/" +
            SignatureDecoderSeeds.DefinitionKotlinName(reader, entry.ArityNames, entry.Handle);
    }

    private void Exit(Key key)
    {
        if (_active.Count == 0 || _active[^1].Key != key)
            throw new InvalidOperationException("delegate decoding path was unwound out of order");
        _active.RemoveAt(_active.Count - 1);
    }

    private readonly record struct Key(string DefinitionPath, int TypeDefinitionRow);
    private sealed record Entry(
        Key Key,
        MetadataReader Reader,
        ArityNames ArityNames,
        TypeDefinitionHandle Handle);

    private sealed class ExitScope(DelegateDecodingContext owner, Key key) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.Exit(key);
        }
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
    private readonly ExternalSignatureDecoderCache _externalSignatureDecoders;
    private readonly string _definitionPath;
    private readonly DelegateDecodingContext _delegateDecoding;
    private readonly IReadOnlyDictionary<TypeDefinitionHandle, int> _semanticTypeNames;
    private readonly bool _restoreKotlinCollections;
    private readonly IReadOnlyDictionary<string, TypeDefinitionHandle> _delegateDefinitions;
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
    private readonly IReadOnlySet<string> _seedValueTypeNames;
    private readonly HashSet<string> _observedValueTypeNames = new(StringComparer.Ordinal);

    public SignatureDecoder(
        MetadataReader md,
        NameTable names,
        MetadataAttributes attrs,
        ArityNames arityNames,
        DelegateReferenceCatalog delegateCatalog,
        CompanionReferenceCatalog companionCatalog,
        InnerReferenceCatalog innerCatalog,
        SignatureDecoderSeeds seeds,
        ExternalSignatureDecoderCache externalSignatureDecoders,
        string definitionPath,
        IReadOnlyDictionary<TypeDefinitionHandle, int>? semanticTypeNames = null,
        DelegateDecodingContext? delegateDecoding = null)
    {
        _md = md;
        _names = names;
        _attrs = attrs;
        _arityNames = arityNames;
        _delegateCatalog = delegateCatalog;
        _companionCatalog = companionCatalog;
        _innerCatalog = innerCatalog;
        _externalSignatureDecoders = externalSignatureDecoders;
        _definitionPath = Path.GetFullPath(definitionPath);
        _delegateDecoding = delegateDecoding ?? new DelegateDecodingContext();
        _delegateDefinitions = seeds.DelegateDefinitions;
        _seedValueTypeNames = seeds.ValueTypeNames;
        _semanticTypeNames = semanticTypeNames ?? new Dictionary<TypeDefinitionHandle, int>();
        _restoreKotlinCollections = attrs.IsDotKtAssembly;
    }

    internal MetadataAttributes Attributes => _attrs;

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
        if (type.HasClassName && _names.ClassName(type.ClassName) is { } name)
            _observedValueTypeNames.Add(name);
        return type;
    }

    private bool IsValueTypeName(string? name) => name is not null &&
        (_seedValueTypeNames.Contains(name) || _observedValueTypeNames.Contains(name));

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
            span.Argument.Add(new KType.Types.Argument
            {
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
        copy.Argument.Add(semanticTypeArguments.Select(t => new KType.Types.Argument
        {
            Projection = KType.Types.Argument.Types.Projection.Inv,
            Type = t,
        }));
        if (copy.FlexibleUpperBound is { } upper)
            upper.Argument.Add(semanticTypeArguments.Select(t => new KType.Types.Argument
            {
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
    public KType GetPrimitiveType(PrimitiveTypeCode code) => code switch
    {
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
        MetadataReader reader, TypeDefinitionHandle handle) =>
        SignatureDecoderSeeds.DefinitionKotlinPath(reader, _arityNames, handle);

    string DefinitionKotlinName(MetadataReader reader, TypeDefinitionHandle handle)
        => SignatureDecoderSeeds.DefinitionKotlinName(reader, _arityNames, handle);

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
        // SRM decodes a custom modifier's TypeDefOrRef before calling GetModifiedType, whose Kotlin projection drops
        // the modifier. Do not expand a delegate graph that cannot affect the resulting KType.
        if (rawTypeKind == (byte)SignatureTypeKind.Unknown) return Any(nullable: true);
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
        if (rawTypeKind == (byte)SignatureTypeKind.Unknown) return Any(nullable: true);
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
        return full switch
        {
            "System.String" => Platform("kotlin.String"),
            "System.Object" => Platform("kotlin.Any"),
            _ => rawTypeKind == (byte)SignatureTypeKind.Class ? Platform(className) : MarkValueTypeIfStated(rawTypeKind, Named(className)),
        };
    }
    public KType GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public KType DecodeEntity(EntityHandle handle, GenericContext context, bool platform) =>
        handle.Kind switch
        {
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

    // True for a type in the compiler's reserved `DotKt.Runtime.CompilerServices` namespace: a physical slot carrier
    // bir2cir attaches to an emitted TypeDef, never part of the Kotlin surface a consumer resolves against.
    public bool IsCompilerOwnedSlotCarrier(KType type) =>
        type.HasClassName && _names.ClassName(type.ClassName) is string fqn
        && fqn.StartsWith(MetadataAttributes.DotKtNs, System.StringComparison.Ordinal);

    public KType? ArrayElement(KType array)
    {
        if (!array.HasClassName) return null;
        var name = _names.ClassName(array.ClassName);
        if (name == "kotlin.Array" &&
            array.Argument.Count == 1 &&
            array.Argument[0].Type is { } element)
            return element.Clone();
        var elementName = name switch
        {
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
                copy = byteValue switch
                {
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
        annotation.Argument.Add(new Annotation.Types.Argument
        {
            NameId = _names.String("count"),
            Value = new Annotation.Types.Argument.Types.Value
            {
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
        return value switch
        {
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
        var type = NamedCarrierClassifier(name);
        if (f.Args is not null)
            type.Argument.Add(f.Args.Select(a => a is TypeNode.Star
                ? new KType.Types.Argument { Projection = KType.Types.Argument.Types.Projection.Star }
                : new KType.Types.Argument
                {
                    Projection = KType.Types.Argument.Types.Projection.Inv,
                    Type = FromTypeNode(a),
                }));
        return type;
    }

    // Carrier TypeNodes can name an exact nested metadata path with '+'. Keep the package/class boundary and each
    // nesting segment explicit in the KLIB qualified-name table; treating the whole suffix as one top-level simple
    // name creates a different Kotlin classifier even when its rendered text looks similar.
    private KType NamedCarrierClassifier(string name)
    {
        var firstNested = name.IndexOf('+');
        if (firstNested < 0) return Named(name);
        var outer = name[..firstNested];
        var packageEnd = outer.LastIndexOf('.');
        var package = packageEnd < 0 ? "" : outer[..packageEnd];
        var outerSimple = packageEnd < 0 ? outer : outer[(packageEnd + 1)..];
        var nested = name[(firstNested + 1)..].Split('+', StringSplitOptions.None);
        return new KType { ClassName = _names.Class(package, nested.Prepend(outerSimple)) };
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
            result.Argument.Add(new KType.Types.Argument
            {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = FromTypeNode(parameter),
            });
        if (function.Suspend)
        {
            var continuation = Named("kotlin.coroutines.Continuation");
            continuation.Argument.Add(new KType.Types.Argument
            {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = FromTypeNode(function.Ret),
            });
            result.Argument.Add(new KType.Types.Argument
            {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = continuation,
            });
            result.Argument.Add(new KType.Types.Argument
            {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = Any(nullable: true),
            });
        }
        else
        {
            result.Argument.Add(new KType.Types.Argument
            {
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
        result.Argument.Add(new KType.Types.Argument
        {
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
        // A CONSTRUCTED value type holds a byte but is never annotated by it, so the bare carrier classifier here is
        // the right question for BOTH: can this node's `?` ride the byte at all.
        TypeNode.Fqn f => ConsumesNullability(NamedCarrierClassifier(NormalizeKotlinName(f.Name))),
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
    private KType Array(KType element)
    {
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
        using var active = _delegateDecoding.Enter(
            _definitionPath, _md, _arityNames, handle);
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

        var source = _externalSignatureDecoders.Get(entry.DefinitionPath);
        var md = source.Reader;
        var handle = MetadataTokens.TypeDefinitionHandle(entry.TypeDefinitionRow);
        if (handle.IsNil || MetadataTokens.GetRowNumber(handle) > md.TypeDefinitions.Count)
            throw new InvalidDataException(
                $"delegate catalog contains an invalid TypeDef row for {entry.MetadataName}");
        var decoder = new SignatureDecoder(
            md,
            _names,
            source.Attributes,
            source.ArityNames,
            _delegateCatalog,
            _companionCatalog,
            _innerCatalog,
            source.Seeds,
            _externalSignatureDecoders,
            entry.DefinitionPath,
            delegateDecoding: _delegateDecoding);
        var shape = decoder.DecodeDelegate(handle);
        _externalDelegateShapes[key] = shape;
        return shape.Clone();
    }

    private KType Function(IEnumerable<KType> parameters, KType returnType)
    {
        var ps = parameters.ToList();
        var result = Named($"kotlin.Function{ps.Count}");
        foreach (var item in ps.Append(returnType))
            result.Argument.Add(new KType.Types.Argument
            {
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

}
