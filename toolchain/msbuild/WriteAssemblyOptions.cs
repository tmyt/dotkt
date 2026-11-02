using System.Text;
using System.Text.Json;
using Microsoft.Build.Framework;

namespace DotKt.Build;

// Transport SDK-selected physical facts, not source fragments or Kotlin semantics. In particular, passing an
// ITaskItem[] preserves empty strings and punctuation without reinterpreting them as MSBuild item separators.
public sealed class WriteAssemblyOptions : Microsoft.Build.Utilities.Task
{
    [Required] public string OutputFile { get; set; } = "";
    [Required] public string OutputFileName { get; set; } = "";
    [Required] public string OutputType { get; set; } = "";
    [Required] public string SdkResourceAssembly { get; set; } = "";
    public bool Deterministic { get; set; }
    public bool JitDebugging { get; set; }
    public bool NoWin32Manifest { get; set; }
    public string Win32Manifest { get; set; } = "";
    public string Win32Resource { get; set; } = "";
    public string ApplicationIcon { get; set; } = "";
    public ITaskItem[] Attributes { get; set; } = [];

    public override bool Execute()
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                outputFileName = OutputFileName, outputType = OutputType, deterministic = Deterministic, jitDebugging = JitDebugging,
                sdkResourceAssembly = SdkResourceAssembly, noWin32Manifest = NoWin32Manifest,
                win32Manifest = Win32Manifest, win32Resource = Win32Resource, applicationIcon = ApplicationIcon,
                attributes = Attributes.Select(a => new
                {
                    type = a.ItemSpec, value = a.GetMetadata("_Parameter1"),
                    isLiteral = a.GetMetadata("_Parameter1_IsLiteral"), typeName = a.GetMetadata("_Parameter1_TypeName")
                })
            });
            if (!File.Exists(OutputFile) || File.ReadAllText(OutputFile) != json)
                File.WriteAllText(OutputFile, json, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: false);
            return false;
        }
    }
}
