using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class RoundtripMetadata
{
    // The logical result is a declaration-owned frozen TypeNode fact. Its
    // metadata transport is not parsed or reconstructed at this validation clock.
    internal static void RequireSuspendResults(IEnumerable<BirFileDeclaration> roots)
    {
        foreach (var file in roots)
        {
            Require(file.Methods.OrEmpty());
            foreach (var type in file.Types.OrEmpty()) Require(type.Methods.OrEmpty());
        }

        static void Require(IEnumerable<BirFunctionDeclaration> methods)
        {
            foreach (var method in methods)
                if (method.Mods.OrDefault()?.Suspend.OrDefault() == true && !method.SuspendResult.HasValue)
                    throw new InvalidOperationException(
                        $"suspend declaration '{method.Name.OrDefault()}' did not preserve its logical result");
        }
    }
}
