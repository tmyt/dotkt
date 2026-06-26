// bir2cir — lower Backend IR (BIR) JSON into CLR IR (CIR) JSON.
//
// The default output is BIR-compatible CIR so existing ilemit-based pipelines keep
// working while lowering responsibilities move into this stage.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

static class Bir2Cir
{
    static int Main(string[] args)
    {
        try
        {
            var options = DriverOptions.Parse(args);
            new Pipeline(options).Run();
            return 0;
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("usage: bir2cir <out-dir> [--compat-bir|--native-cir] [--ref <dll>]... <file.bir.json>...");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"bir2cir: {ex.Message}");
            return 1;
        }
    }
}

sealed class Pipeline
{
    readonly DriverOptions _options;

    public Pipeline(DriverOptions options) => _options = options;

    public void Run()
    {
        Directory.CreateDirectory(_options.OutDir);

        var birFiles = LoadBirFiles(_options.Inputs);
        var refs = ReferenceMetadataIndex.Build(_options.References);
        var cirFiles = TransformFiles(birFiles, refs);
        WriteCirFiles(cirFiles);

        Console.Error.WriteLine(
            $"bir2cir: lowered {birFiles.Count} BIR file(s) -> {_options.OutDir} ({refs.Count} ref(s), mode: {_options.ModeName})");
    }

    static List<BirFile> LoadBirFiles(IReadOnlyList<string> inputs)
    {
        var files = new List<BirFile>();
        foreach (var input in inputs)
        {
            var path = Path.GetFullPath(input);
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            files.Add(new BirFile(path, json));
        }

        return files;
    }

    List<CirFile> TransformFiles(IReadOnlyList<BirFile> birFiles, ReferenceMetadataIndex refs)
    {
        var files = new List<CirFile>();
        foreach (var bir in birFiles)
        {
            var outputName = OutputNameFor(bir.Path);
            var json = _options.Mode == OutputMode.CompatBir
                ? bir.Json
                : NativeCirEnvelope.Create(bir, refs).ToJsonString(JsonOptions.Indented);
            files.Add(new CirFile(outputName, json));
        }

        return files;
    }

    void WriteCirFiles(IReadOnlyList<CirFile> files)
    {
        foreach (var file in files)
            File.WriteAllText(Path.Combine(_options.OutDir, file.OutputName), file.Json);
    }

    static string OutputNameFor(string inputPath)
    {
        var name = Path.GetFileName(inputPath);
        if (name.EndsWith(".bir.json", StringComparison.Ordinal))
            return name[..^".bir.json".Length] + ".cir.json";
        if (name.EndsWith(".json", StringComparison.Ordinal))
            return name[..^".json".Length] + ".cir.json";
        return name + ".cir.json";
    }
}

sealed record DriverOptions(string OutDir, OutputMode Mode, IReadOnlyList<string> References, IReadOnlyList<string> Inputs)
{
    public string ModeName => Mode == OutputMode.CompatBir ? "compat-bir" : "native-cir";

    public static DriverOptions Parse(string[] args)
    {
        if (args.Length < 2)
            throw new UsageException("bir2cir: missing output directory or input files");

        var outDir = args[0];
        var refs = new List<string>();
        var inputs = new List<string>();
        var mode = OutputMode.CompatBir;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ref" when i + 1 < args.Length:
                    refs.Add(Path.GetFullPath(args[++i]));
                    break;
                case "--ref":
                    throw new UsageException("bir2cir: --ref requires a DLL path");
                case "--compat-bir":
                    mode = OutputMode.CompatBir;
                    break;
                case "--native-cir":
                    mode = OutputMode.NativeCir;
                    break;
                default:
                    inputs.Add(args[i]);
                    break;
            }
        }

        if (inputs.Count == 0)
            throw new UsageException("bir2cir: no BIR input files");

        return new DriverOptions(outDir, mode, refs, inputs);
    }
}

enum OutputMode
{
    CompatBir,
    NativeCir,
}

sealed record BirFile(string Path, string Json);
sealed record CirFile(string OutputName, string Json);

sealed class ReferenceMetadataIndex
{
    readonly List<ReferenceAssembly> _assemblies;

    ReferenceMetadataIndex(List<ReferenceAssembly> assemblies) => _assemblies = assemblies;

    public int Count => _assemblies.Count;
    public IReadOnlyList<ReferenceAssembly> Assemblies => _assemblies;

    public static ReferenceMetadataIndex Build(IReadOnlyList<string> refs)
    {
        var assemblies = new List<ReferenceAssembly>();
        foreach (var reference in refs)
        {
            if (!File.Exists(reference))
                throw new UsageException($"bir2cir: reference not found: {reference}");

            var identity = AssemblyName.GetAssemblyName(reference);
            assemblies.Add(new ReferenceAssembly(
                reference,
                identity.Name ?? Path.GetFileNameWithoutExtension(reference),
                identity.Version?.ToString() ?? ""));
        }

        return new ReferenceMetadataIndex(assemblies);
    }
}

sealed record ReferenceAssembly(string Path, string Name, string Version);

static class NativeCirEnvelope
{
    public static JsonObject Create(BirFile bir, ReferenceMetadataIndex refs)
    {
        return new JsonObject
        {
            ["cirVersion"] = 1,
            ["mode"] = "native-cir-skeleton",
            ["sourcePath"] = bir.Path,
            ["references"] = new JsonArray(refs.Assemblies
                .Select(r => new JsonObject
                {
                    ["path"] = r.Path,
                    ["name"] = r.Name,
                    ["version"] = r.Version,
                })
                .Cast<JsonNode>()
                .ToArray()),
            ["bir"] = JsonNode.Parse(bir.Json),
        };
    }
}

static class JsonOptions
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}

sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}
