using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotKt.Build;

// Roslyn owns VERSIONINFO, default manifests and icon conversion. Its resource APIs return RES directly:
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
            options.NativeResourceSdkAssembly = typeof(Compilation).Assembly.Location;
            var versions = options.Versions();
            // Resolve wildcard identity once, shared by managed metadata and Roslyn's resource generation.
            options.Attributes.RemoveAll(a => SdkAssemblyOptions.IsAttribute(a.Type, "AssemblyVersion"));
            options.Attributes.Add(new("System.Reflection.AssemblyVersionAttribute", versions.Identity.ToString(), "", ""));
            if (!string.IsNullOrEmpty(options.Win32Resource))
            {
                if (!string.IsNullOrEmpty(options.Win32Manifest) || !string.IsNullOrEmpty(options.ApplicationIcon))
                    throw new InvalidDataException("Win32Resource cannot be combined with ApplicationManifest or ApplicationIcon");
                var path = Path.GetFullPath(options.Win32Resource);
                switch (ResourceForm(path))
                {
                    case "RES":
                        options.NativeResource = path;
                        break;
                    case "COFF":
                        throw new InvalidDataException($"COFF Win32Resource '{path}' is not supported. Set Win32Resource to the original .res file.");
                    default:
                        throw new InvalidDataException($"Unrecognized Win32 resource file '{path}'. Set Win32Resource to a compiled .res file.");
                }
            }
            else
            {
                var isDll = string.Equals(options.OutputType, "Library", StringComparison.OrdinalIgnoreCase);
                var kind = isDll ? OutputKind.DynamicallyLinkedLibrary : OutputKind.ConsoleApplication;
                var compilation = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(options.OutputFileName),
                    options: new CSharpCompilationOptions(kind, moduleName: options.OutputFileName));
                using var manifest = string.IsNullOrEmpty(options.Win32Manifest) ? null : File.OpenRead(options.Win32Manifest);
                using var icon = string.IsNullOrEmpty(options.ApplicationIcon) ? null : File.OpenRead(options.ApplicationIcon);
                using var resources = compilation.CreateDefaultWin32Resources(versionResource: false,
                    noManifest: options.NoWin32Manifest || (isDll && manifest == null), manifestContents: manifest, iconInIcoFormat: icon);
                SdkVersionResourceWriter.Append(resources, options, versions.Identity, isDll);
                resources.Position = 0;
                using (var output = File.Create(ResourceFile)) resources.CopyTo(output);
                options.NativeResource = Path.GetFullPath(ResourceFile);
            }
            File.WriteAllText(OutputFile, JsonSerializer.Serialize(options, SdkAssemblyOptionsJsonContext.Default.SdkAssemblyOptions), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: false);
            return false;
        }
    }

    static string? ResourceForm(string path)
    {
        // Keep format detection on the SDK side, rather than interpreting a file signature in ilemit.
        var detect = typeof(Compilation).GetMethod("DetectWin32ResourceForm",
            BindingFlags.Static | BindingFlags.NonPublic, [typeof(Stream)])
            ?? throw new InvalidOperationException("The selected .NET SDK does not expose Win32 resource format detection.");
        using var input = File.OpenRead(path);
        try { return detect.Invoke(null, [input])?.ToString(); }
        catch (TargetInvocationException ex) when (ex.InnerException is { } cause)
        {
            throw new InvalidDataException($"Cannot detect Win32 resource format for '{path}': {cause.Message}", cause);
        }
    }
}
