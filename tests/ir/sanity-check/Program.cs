using DotKt.Bir;
using DotKt.Cir;
using System.Text.Json;

// These are validator fixtures, not complete emit-ready assemblies. Call the normative checker in the
// built ilemit directly: an unrelated failure later in emission must not count as sanity acceptance.
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: SanityCheck [--source-stamps] <fixture>...");
    return 1;
}

try
{
    var sourceStamps = args[0] == "--source-stamps";
    foreach (var file in sourceStamps ? args.Skip(1) : args)
    {
        if (sourceStamps)
        {
            // The actual bir2cir pre-lowering checkpoint still owns current BIR stamps.
            // Never pass a source tree through the physical CIR decoder.
            using var source = JsonDocument.Parse(File.ReadAllBytes(file), BirJson.DocOptions);
            IrSanity.CheckSourceStamps(new[] { source.RootElement });
        }
        else
        {
            var declaration = DeclarationJson.ReadFile(file);
            IrSanity.Check(new[] { declaration });
        }
        Console.WriteLine($"IrSanity accepted: {file}");
    }
    return 0;
}
catch (IrSanityException ex)
{
    Console.Error.WriteLine($"{ex.Decl}: sanity: {ex.Message}");
    return 1;
}
