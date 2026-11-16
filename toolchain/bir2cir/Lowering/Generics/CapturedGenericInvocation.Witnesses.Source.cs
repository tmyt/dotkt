using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CapturedGenericInvocation
{
    // The module clock is immediately before existential projection. Preserve
    // its allocated declaration parameters in the existing witness authority;
    // later invocation binding must not recapture their projected value types.
    internal static void CaptureParameterWitnesses(IReadOnlyList<BirFileDeclaration> roots)
    {
        void Capture(IEnumerable<BirFunctionDeclaration> methods)
        {
            foreach (var method in methods)
                if (method.DeclarationId.HasValue)
                {
                    _parameterWitnesses[method.DeclarationId.Value] = method.Params.Value
                        .Select(parameter => BirGraphValue.CopyType(parameter.Type.OrDefault())).ToArray();
                    _methodWitnessFrames[method.DeclarationId.Value] = method.NullableRepresentationFrame.OrDefault();
                }
        }
        foreach (var root in roots)
        {
            Capture(root.Methods.OrEmpty());
            foreach (var owner in root.Types.OrEmpty()) Capture(owner.Methods.OrEmpty());
        }
    }
}
