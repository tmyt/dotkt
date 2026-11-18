using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Build.Framework;

namespace DotKt.Build;

public sealed class WriteCompilerPluginArguments : Microsoft.Build.Utilities.Task
{
    [Required] public string OutputFile { get; set; } = "";
    public ITaskItem[] Plugins { get; set; } = [];
    public ITaskItem[] Options { get; set; } = [];
    [Output] public string Fingerprint { get; set; } = "";

    public override bool Execute()
    {
        try
        {
            var args = new List<string>();
            var hashes = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var jar in Plugins)
            {
                var path = jar.GetMetadata("FullPath");
                if (!seen.Add(path)) continue;
                args.Add("-Xplugin=" + path);
                using var input = File.OpenRead(path);
                hashes.Add(Convert.ToHexString(SHA256.HashData(input)));
            }
            foreach (var option in Options)
            {
                args.Add("-P");
                args.Add($"plugin:{option.ItemSpec}={option.GetMetadata("Value")}");
            }
            // Option values are text, not MSBuild paths: an item transform would
            // normalize literal backslashes before Kotlin's argfile parser sees them.
            var content = string.Join("\n", args.Select(arg => "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "\n";
            if (!File.Exists(OutputFile) || File.ReadAllText(OutputFile) != content)
                File.WriteAllText(OutputFile, content, new UTF8Encoding(false));
            Fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { args, hashes })));
            return true;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: false);
            return false;
        }
    }
}
