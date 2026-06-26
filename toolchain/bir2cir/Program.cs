// bir2cir — lower Backend IR (BIR) JSON into CLR IR (CIR) JSON.
//
// Phase 1 is intentionally a compatibility skeleton: it parses the BIR input, accepts
// referenced assemblies as lowering inputs, and writes BIR-compatible CIR JSON files.
// Later phases move CLR projection, type lowering, inline expansion, and suspend
// lowering here. Suspend should first lower to CLR async/await-shaped CIR, then to
// state-machine/IL-oriented CIR, so ilemit can shrink into CIR -> IL emission.
using System.Text.Json;

static class Bir2Cir
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: bir2cir <out-dir> [--ref <dll>]... <file.bir.json>...");
            return 1;
        }

        var outDir = args[0];
        Directory.CreateDirectory(outDir);

        var refs = new List<string>();
        var inputs = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--ref" && i + 1 < args.Length)
            {
                refs.Add(Path.GetFullPath(args[++i]));
                continue;
            }

            inputs.Add(args[i]);
        }

        if (inputs.Count == 0)
        {
            Console.Error.WriteLine("bir2cir: no BIR input files");
            return 1;
        }

        foreach (var reference in refs)
        {
            if (!File.Exists(reference))
            {
                Console.Error.WriteLine($"bir2cir: reference not found: {reference}");
                return 1;
            }
        }

        foreach (var input in inputs)
        {
            var fullInput = Path.GetFullPath(input);
            JsonDocument.Parse(File.ReadAllText(fullInput)).Dispose();

            var name = Path.GetFileName(fullInput);
            if (name.EndsWith(".bir.json", StringComparison.Ordinal))
                name = name[..^".bir.json".Length] + ".cir.json";
            else if (name.EndsWith(".json", StringComparison.Ordinal))
                name = name[..^".json".Length] + ".cir.json";
            else
                name += ".cir.json";

            File.Copy(fullInput, Path.Combine(outDir, name), overwrite: true);
        }

        Console.Error.WriteLine($"bir2cir: lowered {inputs.Count} BIR file(s) -> {outDir} ({refs.Count} ref(s))");
        return 0;
    }
}
