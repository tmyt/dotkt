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

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                Convert(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
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

        var inputs = File.ReadLines(responseFile)
            .Select(x => x.Trim())
            .Where(x => x.Length != 0)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
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
        var delegateCatalog = DelegateReferenceCatalog.Discover(inputs);
        var delegateCatalogJson = delegateCatalog.Serialize();
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
        File.WriteAllText(catalogPath, delegateCatalogJson);
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
            "  dll2klib <reference.dll> <output.klib>\n" +
            "  dll2klib --out <directory> [--jobs <N>] @<references.rsp>\n" +
            "  --jobs 0 starts one worker per stale reference");
        return 2;
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
        var fragments = new AssemblyScanner(md, arityNames, delegateCatalog).Scan();

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
            foreach (var handle in md.TypeDefinitions)
            {
                var def = md.GetTypeDefinition(handle);
                if (!def.GetDeclaringType().IsNil) continue;
                var attrs = def.Attributes & TypeAttributes.VisibilityMask;
                if (attrs != TypeAttributes.Public) continue;
                var metadataName = md.GetString(def.Name);
                var tick = metadataName.IndexOf('`');
                var simple = tick < 0 ? metadataName : metadataName[..tick];
                var arity = tick < 0 || !int.TryParse(metadataName[(tick + 1)..], out var parsed)
                    ? 0 : parsed;
                var key = string.IsNullOrEmpty(md.GetString(def.Namespace))
                    ? simple
                    : md.GetString(def.Namespace) + "." + simple;
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
        var local = md.TypeDefinitions
            .Select(h => md.GetTypeDefinition(h))
            .Where(d => d.GetDeclaringType().IsNil)
            .Select(d => (Namespace: md.GetString(d.Namespace), Name: md.GetString(d.Name)))
            .GroupBy(x => FullName(x.Namespace, Strip(x.Name)), StringComparer.Ordinal);
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
    private readonly MetadataReader _md;
    private readonly MetadataAttributes _attrs;
    private readonly ArityNames _arityNames;
    private readonly DelegateReferenceCatalog _delegateCatalog;

    public AssemblyScanner(
        MetadataReader md,
        ArityNames arityNames,
        DelegateReferenceCatalog delegateCatalog)
    {
        _md = md;
        _attrs = new MetadataAttributes(md);
        _arityNames = arityNames;
        _delegateCatalog = delegateCatalog;
    }

    public IReadOnlyList<Fragment> Scan()
    {
        var visible = _md.TypeDefinitions
            .Select(h => (Handle: h, Definition: _md.GetTypeDefinition(h)))
            .Where(x => IsPublicTopLevel(x.Definition))
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
                _md, names, _attrs, _arityNames, _delegateCatalog);

            foreach (var (handle, def) in package.OrderBy(x => _md.GetString(x.Definition.Name), StringComparer.Ordinal))
            {
                try
                {
                    if (_attrs.Has(handle, MetadataAttributes.DotKtNs + "KotlinFileClassAttribute"))
                    {
                        ReadFileFacade(handle, def, fragment.Package, names, signatures);
                        continue;
                    }
                    var klass = ReadClass(handle, def, names, signatures);
                    fragment.Class.Add(klass);
                    fragment.ClassName.Add(klass.FqName);
                    AddStaticCompanion(handle, klass, fragment, names);
                    ReadNestedClasses(handle, klass, fragment, names, signatures);
                    ReadCSharpExtensions(handle, def, fragment.Package, names, signatures);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"dll2klib: warning: skipped {FullName(def)}: {ex.Message}");
                    if (Environment.GetEnvironmentVariable("DOTKT_DLL2KLIB_DEBUG") == "1")
                        Console.Error.WriteLine(ex);
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
                _md, names, _attrs, _arityNames, _delegateCatalog);
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

    private Class ReadClass(TypeDefinitionHandle handle, TypeDefinition def, NameTable names, SignatureDecoder signatures)
    {
        var metadataName = _md.GetString(def.Name);
        var metadataNamespace = _md.GetString(def.Namespace);
        var kotlinName = _arityNames.Simple(metadataNamespace, metadataName);
        var isInterface = (def.Attributes & TypeAttributes.Interface) != 0;
        var isEnum = IsSystemType(def.BaseType, "System", "Enum");
        var isAnnotation = IsAttributeType(handle);
        var isClrExceptionRoot =
            metadataNamespace == "System" &&
            metadataName == "Exception";
        var isObject = _attrs.Has(handle, MetadataAttributes.DotKtNs + "KotlinObjectAttribute");
        var isKotlinSealed = _attrs.Has(handle, MetadataAttributes.DotKtNs + "KotlinSealedAttribute");
        var isKotlinValue = _attrs.Has(handle, MetadataAttributes.DotKtNs + "KotlinValueAttribute");
        var kind = isObject ? 5 : isInterface ? 1 : isEnum ? 2 : isAnnotation ? 4 : 0;
        var modality = isKotlinSealed ? 3
            : kind == 1 || (def.Attributes & TypeAttributes.Abstract) != 0 ? 2
            : (def.Attributes & TypeAttributes.Sealed) == 0 ? 1 : 0;
        var result = new Class {
            FqName = ClassName(handle, names),
            Flags = Flags.Declaration(
                modality,
                kind,
                isValue: isKotlinValue,
                isFun: _attrs.Has(handle, MetadataAttributes.DotKtNs + "KotlinFunInterfaceAttribute"),
                hasEnumEntries: isEnum),
        };
        result.ClassAnnotation.Add(ClrExternalAnnotation(names, MetadataTypeName(handle)));
        result.Flags |= 1;

        var typeParameterIds = new Dictionary<GenericParameterHandle, int>();
        foreach (var gpHandle in def.GetGenericParameters())
        {
            var gp = _md.GetGenericParameter(gpHandle);
            var id = gp.Index;
            typeParameterIds[gpHandle] = id;
            result.TypeParameter.Add(new TypeParameter {
                Id = id,
                Name = names.String(_md.GetString(gp.Name)),
                Variance = (gp.Attributes & GenericParameterAttributes.VarianceMask) switch {
                    GenericParameterAttributes.Covariant => TypeParameter.Types.Variance.Out,
                    GenericParameterAttributes.Contravariant => TypeParameter.Types.Variance.In,
                    _ => TypeParameter.Types.Variance.Inv,
                },
            });
        }

        var typeContext = new GenericContext(handle, default, typeParameterIds);
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

        foreach (var methodHandle in def.GetMethods())
        {
            var method = _md.GetMethodDefinition(methodHandle);
            if (!IsPublicOrProtected(method.Attributes)) continue;
            var name = _md.GetString(method.Name);
            if (accessorMethods.Contains(methodHandle)) continue;
            var context = new GenericContext(handle, methodHandle, typeParameterIds);
            var sig = method.DecodeSignature(signatures, context);
            if (name == ".ctor")
            {
                result.Constructor.Add(new Constructor {
                    Flags = Flags.Visibility(method.Attributes),
                    ValueParameter = { Parameters(methodHandle, method, sig.ParameterTypes, names, signatures, context) },
                });
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
            }
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
            var childDef = _md.GetTypeDefinition(childHandle);
            if (!IsPublicNested(childDef)) continue;
            try
            {
                var child = ReadClass(childHandle, childDef, names, signatures);
                parent.NestedClassName.Add(names.String(StripArity(_md.GetString(childDef.Name))));
                fragment.Class.Add(child);
                fragment.ClassName.Add(child.FqName);
                AddStaticCompanion(childHandle, child, fragment, names);
                ReadNestedClasses(childHandle, child, fragment, names, signatures);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"dll2klib: warning: skipped nested {KotlinFullName(childHandle)}: {ex.Message}");
            }
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

    private void AddStaticCompanion(
        TypeDefinitionHandle ownerHandle,
        Class owner,
        PackageFragment fragment,
        NameTable names)
    {
        // Preserve Kotlin's companion vocabulary for both CLR classes and
        // companion members flattened by the DotKt ABI, while retaining the
        // standard KLIB static declaration directly on Type. The companion is
        // a metadata view of the same CLR slots; Kotlin objects remain objects.
        if (_attrs.Has(ownerHandle, MetadataAttributes.DotKtNs + "KotlinObjectAttribute") ||
            _md.GetTypeDefinition(ownerHandle).GetNestedTypes().Any(h =>
                StripArity(_md.GetString(_md.GetTypeDefinition(h).Name)) == "Companion"))
            return;
        var functions = owner.Function.Where(f => (f.Flags & (1 << 18)) != 0).ToList();
        var properties = owner.Property.Where(p => (p.Flags & (1 << 19)) != 0).ToList();
        if (functions.Count == 0 && properties.Count == 0) return;

        var companionName = names.String("Companion");
        owner.CompanionObjectName = companionName;
        owner.NestedClassName.Add(companionName);
        var companion = new Class {
            FqName = CompanionClassName(ownerHandle, names),
            Flags = Flags.Declaration(modality: 0, kind: 6),
        };
        companion.ClassAnnotation.Add(ClrExternalAnnotation(names, MetadataTypeName(ownerHandle)));
        companion.Flags |= 1;
        foreach (var function in functions)
        {
            var clone = function.Clone();
            clone.Flags &= ~(1 << 18);
            companion.Function.Add(clone);
        }
        foreach (var property in properties)
        {
            var clone = property.Clone();
            clone.Flags &= ~(1 << 19);
            companion.Property.Add(clone);
        }
        fragment.Class.Add(companion);
        fragment.ClassName.Add(companion.FqName);
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
                ? StripOuterNullability(exact)
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

    private static TypeNode StripOuterNullability(TypeNode type) => type switch {
        TypeNode.Nullable n => n.Of,
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
        var chain = new Stack<string>();
        var current = handle;
        string package = "";
        while (!current.IsNil)
        {
            var def = _md.GetTypeDefinition(current);
            var parent = def.GetDeclaringType();
            if (parent.IsNil) package = _md.GetString(def.Namespace);
            chain.Push(_arityNames.Simple(
                parent.IsNil ? _md.GetString(def.Namespace) : package,
                _md.GetString(def.Name)));
            current = parent;
        }
        return names.Class(package, chain);
    }

    private int CompanionClassName(TypeDefinitionHandle handle, NameTable names)
    {
        var chain = new Stack<string>();
        var current = handle;
        string package = "";
        while (!current.IsNil)
        {
            var def = _md.GetTypeDefinition(current);
            var parent = def.GetDeclaringType();
            if (parent.IsNil) package = _md.GetString(def.Namespace);
            chain.Push(_arityNames.Simple(
                parent.IsNil ? _md.GetString(def.Namespace) : package,
                _md.GetString(def.Name)));
            current = parent;
        }
        return names.Class(package, chain.Append("Companion"));
    }

    private string KotlinFullName(TypeDefinitionHandle handle)
    {
        var chain = new Stack<string>();
        var current = handle;
        string package = "";
        while (!current.IsNil)
        {
            var def = _md.GetTypeDefinition(current);
            var parent = def.GetDeclaringType();
            if (parent.IsNil) package = _md.GetString(def.Namespace);
            chain.Push(_arityNames.Simple(
                parent.IsNil ? _md.GetString(def.Namespace) : package,
                _md.GetString(def.Name)));
            current = parent;
        }
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
    public static int Declaration(int modality, int kind, bool isValue = false, bool isFun = false, bool hasEnumEntries = false) =>
        6 | (modality << 4) | (kind << 6)
        | (isValue ? 1 << 13 : 0) | (isFun ? 1 << 14 : 0) | (hasEnumEntries ? 1 << 15 : 0);
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

internal sealed class SignatureDecoder : ISignatureTypeProvider<KType, GenericContext>
{
    private readonly MetadataReader _md;
    private readonly NameTable _names;
    private readonly MetadataAttributes _attrs;
    private readonly ArityNames _arityNames;
    private readonly DelegateReferenceCatalog _delegateCatalog;
    private readonly bool _restoreKotlinCollections;
    private readonly Dictionary<string, TypeDefinitionHandle> _delegateDefinitions = new(StringComparer.Ordinal);
    private readonly Dictionary<KType, DelegateCatalogEntry> _externalDelegateTypes =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, KType> _externalDelegateShapes = new(StringComparer.Ordinal);

    public SignatureDecoder(
        MetadataReader md,
        NameTable names,
        MetadataAttributes attrs,
        ArityNames arityNames,
        DelegateReferenceCatalog delegateCatalog)
    {
        _md = md;
        _names = names;
        _attrs = attrs;
        _arityNames = arityNames;
        _delegateCatalog = delegateCatalog;
        _restoreKotlinCollections = attrs.IsDotKtAssembly;
        foreach (var handle in md.TypeDefinitions)
        {
            var def = md.GetTypeDefinition(handle);
            if (IsSystemType(def.BaseType, "System", "MulticastDelegate"))
                _delegateDefinitions[MetadataFullName(handle)] = handle;
        }
    }

    public KType GetArrayType(KType elementType, ArrayShape shape) => Array(elementType);
    public KType GetByReferenceType(KType elementType) => ByRef(elementType);
    public KType GetFunctionPointerType(MethodSignature<KType> signature) => Any(nullable: true);
    public KType GetGenericInstantiation(KType genericType, ImmutableArray<KType> typeArguments)
    {
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
            return span;
        }
        if (genericName is not null && IsKnownDelegate(genericName))
            return ConstructDelegate(genericName, typeArguments);
        var copy = genericType.Clone();
        copy.Argument.Add(typeArguments.Select(t => new KType.Types.Argument {
            Projection = KType.Types.Argument.Types.Projection.Inv,
            Type = t,
        }));
        if (copy.FlexibleUpperBound is { } upper)
            upper.Argument.Add(typeArguments.Select(t => new KType.Types.Argument {
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
    public KType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var def = reader.GetTypeDefinition(handle);
        var name = _arityNames.Full(reader.GetString(def.Namespace), reader.GetString(def.Name));
        if (_attrs.CarrierType(handle, MetadataAttributes.DotKtNs + "KotlinTypeAttribute") is { } carrier)
            return FromTypeNode(carrier);
        if (_delegateDefinitions.ContainsKey(name) && !def.GetGenericParameters().Any())
            return DecodeDelegate(handle);
        return rawTypeKind == (byte)SignatureTypeKind.Class ? Platform(name) : Named(name);
    }
    public KType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeReference(handle);
        var metadataName = reader.GetString(type.Name);
        var ns = reader.GetString(type.Namespace);
        var full = _arityNames.Full(ns, metadataName);
        var metadataFull = string.IsNullOrEmpty(ns)
            ? StripArity(metadataName)
            : ns + "." + StripArity(metadataName);
        if (_delegateCatalog.TryResolve(reader, handle, out var externalDelegate))
        {
            if (!metadataName.Contains('`'))
                return DecodeExternalDelegate(externalDelegate);
            var marker = rawTypeKind == (byte)SignatureTypeKind.Class
                ? Platform(full)
                : Named(full);
            _externalDelegateTypes[marker] = externalDelegate;
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
            _ => rawTypeKind == (byte)SignatureTypeKind.Class ? Platform(full) : Named(full),
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
            if (consumes)
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
        if (!ConsumesNullability(source)) return source;
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

    private bool ConsumesNullability(KType type)
    {
        if (type.HasTypeParameter) return true;
        if (!type.HasClassName) return false;
        var name = _names.ClassName(type.ClassName);
        return name is not ("kotlin.Unit" or "kotlin.Boolean" or "kotlin.Char" or
            "kotlin.Byte" or "kotlin.UByte" or "kotlin.Short" or "kotlin.UShort" or
            "kotlin.Int" or "kotlin.UInt" or "kotlin.Long" or "kotlin.ULong" or
            "kotlin.Float" or "kotlin.Double");
    }

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
    private KType Platform(string fqName)
    {
        var lower = Named(fqName);
        lower.FlexibleTypeCapabilitiesId = _names.String("dotkt.clr.PlatformType");
        lower.FlexibleUpperBound = Named(fqName, nullable: true);
        return lower;
    }
    private KType FromDefinition(TypeDefinitionHandle handle, bool platform)
    {
        var def = _md.GetTypeDefinition(handle);
        var name = _arityNames.Full(_md.GetString(def.Namespace), _md.GetString(def.Name));
        if (_delegateDefinitions.ContainsKey(name) && !def.GetGenericParameters().Any())
            return DecodeDelegate(handle);
        return platform ? Platform(name) : Named(name);
    }
    private KType FromReference(TypeReferenceHandle handle, bool platform)
    {
        var type = _md.GetTypeReference(handle);
        var metadataName = _md.GetString(type.Name);
        var ns = _md.GetString(type.Namespace);
        var name = _arityNames.Full(ns, metadataName);
        var metadataFull = string.IsNullOrEmpty(ns)
            ? StripArity(metadataName)
            : ns + "." + StripArity(metadataName);
        if (_restoreKotlinCollections && KotlinCollection(name) is string collection)
            name = collection;
        if (_restoreKotlinCollections && metadataFull == "System.IComparable")
            name = "kotlin.Comparable";
        name = name switch {
            "System.String" => "kotlin.String",
            "System.Object" => "kotlin.Any",
            _ => name,
        };
        return platform ? Platform(name) : Named(name);
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
        if (element.HasClassName && _names.ClassName(element.ClassName) is string elementName &&
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
            _delegateCatalog);
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

    private string MetadataFullName(TypeDefinitionHandle handle)
    {
        var def = _md.GetTypeDefinition(handle);
        return FullName(_md.GetString(def.Namespace), StripArity(_md.GetString(def.Name)));
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
