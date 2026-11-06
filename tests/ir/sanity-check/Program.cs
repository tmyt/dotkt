using System.Text.Json;
using DotKt.Bir;

// These are validator fixtures, not complete emit-ready assemblies. Call the normative checker in the
// built ilemit directly: an unrelated failure later in emission must not count as sanity acceptance.
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: SanityCheck <fixture.cir.json>...");
    return 1;
}

try
{
    foreach (var file in args)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file), BirJson.DocOptions);
        IrSanity.Check(new[] { document.RootElement });
        Console.WriteLine($"IrSanity accepted: {file}");
    }
    return 0;
}
catch (IrSanityException ex)
{
    Console.Error.WriteLine($"{ex.Decl}: sanity: {ex.Message}");
    return 1;
}
