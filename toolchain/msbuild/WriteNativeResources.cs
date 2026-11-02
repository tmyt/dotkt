using System.Text;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotKt.Build;

// Roslyn owns VERSIONINFO, default manifests and icon conversion. Its public API returns RES directly:
// no Compilation.Emit, provider DLL, placeholder PDB or replacement of the Kotlin assembly is involved.
public sealed class WriteNativeResources : Microsoft.Build.Utilities.Task
{
    [Required] public string InputFile { get; set; } = "";
    [Required] public string OutputFile { get; set; } = "";
    [Required] public string ResourceFile { get; set; } = "";

    public override bool Execute()
    {
        try
        {
            var options = SdkAssemblyOptions.Read(InputFile);
            var versions = options.Versions();
            // Resolve wildcard identity once, shared by managed metadata and Roslyn's resource generation.
            options.Attributes.RemoveAll(a => a.Type is "System.Reflection.AssemblyVersion" or
                "System.Reflection.AssemblyVersionAttribute");
            options.Attributes.Add(new("System.Reflection.AssemblyVersionAttribute", versions.Identity.ToString(), "", ""));
            if (!string.IsNullOrEmpty(options.Win32Resource))
            {
                if (!string.IsNullOrEmpty(options.Win32Manifest) || !string.IsNullOrEmpty(options.ApplicationIcon))
                    throw new InvalidDataException("Win32Resource cannot be combined with ApplicationManifest or ApplicationIcon");
                options.NativeResource = Path.GetFullPath(options.Win32Resource);
            }
            else
            {
                var source = new StringBuilder();
                foreach (var name in new[] { "AssemblyVersion", "AssemblyFileVersion", "AssemblyInformationalVersion",
                    "AssemblyTitle", "AssemblyDescription", "AssemblyCompany", "AssemblyProduct", "AssemblyCopyright", "AssemblyTrademark" })
                {
                    var value = options.Attribute(name);
                    if (value != null)
                        source.Append("[assembly: System.Reflection.").Append(name).Append("Attribute(")
                            .Append(SymbolDisplay.FormatLiteral(value, quote: true)).AppendLine(")]");
                }
                var isDll = string.Equals(options.OutputType, "Library", StringComparison.OrdinalIgnoreCase);
                var kind = isDll ? OutputKind.DynamicallyLinkedLibrary : OutputKind.ConsoleApplication;
                var compilation = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(options.OutputFileName),
                    [CSharpSyntaxTree.ParseText(source.ToString())],
                    [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                    new CSharpCompilationOptions(kind, moduleName: options.OutputFileName));
                using var manifest = string.IsNullOrEmpty(options.Win32Manifest) ? null : File.OpenRead(options.Win32Manifest);
                using var icon = string.IsNullOrEmpty(options.ApplicationIcon) ? null : File.OpenRead(options.ApplicationIcon);
                using var resources = compilation.CreateDefaultWin32Resources(versionResource: true,
                    noManifest: options.NoWin32Manifest || (isDll && manifest == null), manifestContents: manifest, iconInIcoFormat: icon);
                using (var output = File.Create(ResourceFile)) resources.CopyTo(output);
                options.NativeResource = Path.GetFullPath(ResourceFile);
            }
            File.WriteAllText(OutputFile, JsonSerializer.Serialize(options), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: false);
            return false;
        }
    }
}
