using DotKt.Bir;
using DotKt.Cir;

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
        using var declaration = DeclarationJson.ReadFile(file);
        IrSanity.Check(new[] { declaration });
        Console.WriteLine($"IrSanity accepted: {file}");
    }
    return 0;
}
catch (IrSanityException ex)
{
    Console.Error.WriteLine($"{ex.Decl}: sanity: {ex.Message}");
    return 1;
}
