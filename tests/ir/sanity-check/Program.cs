extern alias source;

using DotKt.Bir;
using DotKt.Cir;

// These are validator fixtures, not complete emit-ready assemblies. Call the normative checker in the
// built ilemit (CIR) and bir2cir (source stamps) directly: an unrelated failure
// later in emission must not count as sanity acceptance.
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
            // The actual owning bir2cir checkpoint owns current BIR stamps.
            // Never pass a source tree through the physical CIR decoder.
            var declaration = source::DotKt.Bir.Lowering.BirGraphJson.ReadFile(File.ReadAllBytes(file));
            source::SourceStampSanity.Check(declaration);
        }
        else
        {
            var declaration = DeclarationJson.ReadFile(file);
            IrSanity.Check(new[] { declaration });
        }
        Console.WriteLine($"{(sourceStamps ? "SourceStampSanity" : "IrSanity")} accepted: {file}");
    }
    return 0;
}
catch (IrSanityException ex)
{
    Console.Error.WriteLine($"{ex.Decl}: sanity: {ex.Message}");
    return 1;
}
catch (source::DotKt.Bir.IrSanityException ex)
{
    Console.Error.WriteLine($"{ex.Decl}: sanity: {ex.Message}");
    return 1;
}
