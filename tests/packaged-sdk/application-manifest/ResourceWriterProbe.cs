using System.Text;
using DotKt.Build;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

static class ResourceWriterProbe
{
    public static void Run()
    {
        var cases = new Dictionary<string, string>[]
        {
            new(),
            new() { ["AssemblyVersion"] = "2.3.4.5" },
            new() { ["AssemblyVersion"] = "2.3.4.5", ["AssemblyFileVersion"] = "6.7.8.9" },
            new() { ["AssemblyFileVersion"] = "1.2.3.4", ["AssemblyInformationalVersion"] = "7.8.9-preview+hash" },
            new() { ["AssemblyTitle"] = "", ["AssemblyCopyright"] = "", ["AssemblyProduct"] = "", ["AssemblyCompany"] = "" },
            new() { ["AssemblyTitle"] = "Title \"quoted\" \\ slash", ["AssemblyDescription"] = "line1\nline2",
                ["AssemblyCompany"] = "会社", ["AssemblyProduct"] = "製品", ["AssemblyCopyright"] = "© 2026", ["AssemblyTrademark"] = "™" },
            new() { ["AssemblyFileVersion"] = "" },
            new() { ["AssemblyFileVersion"] = "invalid", ["AssemblyInformationalVersion"] = "" },
            new() { ["AssemblyFileVersion"] = "1.2.3.4.5" },
            new() { ["AssemblyFileVersion"] = "65536.2.3.4" },
        };
        foreach (var isDll in new[] { false, true })
        foreach (var values in cases)
        {
            var options = new SdkAssemblyOptions { OutputFileName = isDll ? "Example.dll" : "Example.exe",
                OutputType = isDll ? "Library" : "Exe", Deterministic = true };
            foreach (var (name, value) in values)
                options.Attributes.Add(new("System.Reflection." + name + "Attribute", value, "", ""));
            var source = new StringBuilder();
            foreach (var (name, value) in values)
                source.Append("[assembly: System.Reflection.").Append(name).Append("Attribute(")
                    .Append(SymbolDisplay.FormatLiteral(value, quote: true)).AppendLine(")]");
            var kind = isDll ? OutputKind.DynamicallyLinkedLibrary : OutputKind.ConsoleApplication;
            var oracle = CSharpCompilation.Create("Example", [CSharpSyntaxTree.ParseText(source.ToString())],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(kind, moduleName: options.OutputFileName));
            using var expected = oracle.CreateDefaultWin32Resources(true, true, null, null);
            var empty = CSharpCompilation.Create("Example", options: new CSharpCompilationOptions(kind, moduleName: options.OutputFileName));
            using var actual = empty.CreateDefaultWin32Resources(false, true, null, null);
            SdkVersionResourceWriter.Append(actual, options, options.Versions().Identity, isDll);
            actual.Position = 0;
            using var expectedBytes = new MemoryStream();
            using var actualBytes = new MemoryStream();
            expected.CopyTo(expectedBytes);
            actual.CopyTo(actualBytes);
            if (!expectedBytes.ToArray().SequenceEqual(actualBytes.ToArray()))
                throw new Exception("SDK VERSIONINFO writer differs from C# attribute oracle: "
                    + options.OutputType + ": " + string.Join("; ", values.Select(pair => pair.Key + "=" + pair.Value)));
        }
        Console.WriteLine("PASS SDK VERSIONINFO: C# oracle byte parity (defaults, versions, empty/escaped/Unicode strings, invalid file-version text; EXE/DLL)");
    }
}
