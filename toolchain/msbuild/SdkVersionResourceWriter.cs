using System.Reflection;
using Microsoft.CodeAnalysis;

namespace DotKt.Build;

static class SdkVersionResourceWriter
{
    public static void Append(Stream resources, SdkAssemblyOptions options, Version identity, bool isDll)
    {
        // The values are already evaluated SDK facts. Let the selected SDK serialize VERSIONINFO
        // directly, without parsing/binding a C# attribute tree just to recover those same values.
        // member-lookup-residual: selected SDK resource API, not a managed program member
        var writer = typeof(Compilation).Assembly.GetType("Microsoft.CodeAnalysis.Win32ResourceConversions")
            ?.GetMethod("AppendVersionToResourceStream", BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Stream), typeof(bool), typeof(string), typeof(string), typeof(string), typeof(string),
                 typeof(Version), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string)]);
        if (writer?.ReturnType != typeof(void))
            throw new InvalidOperationException("The selected .NET SDK does not expose the expected VERSIONINFO writer.");
        var fileVersion = options.Attribute("AssemblyFileVersion") ?? identity.ToString();
        resources.Position = resources.Length;
        try
        {
            writer.Invoke(null, [resources, isDll, fileVersion, options.OutputFileName, options.OutputFileName,
                options.Attribute("AssemblyInformationalVersion") ?? fileVersion, identity,
                options.Attribute("AssemblyTitle") ?? " ", options.Attribute("AssemblyCopyright") ?? " ",
                options.Attribute("AssemblyTrademark"), options.Attribute("AssemblyProduct"),
                options.Attribute("AssemblyDescription"), options.Attribute("AssemblyCompany")]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } cause)
        {
            throw new InvalidDataException($"Cannot write VERSIONINFO: {cause.Message}", cause);
        }
    }
}
