using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClosureSynthesis
{
    // The synthesis-time verdict is authoritative. This late read only asks whether its actual
    // named class survived intrinsic reconstruction/pruning; it must not classify the fields again.
    internal static void AssertSurvivingCapturesLegal(IEnumerable<BirFileDeclaration> roots)
    {
        if (_pendingCaptureRefusals.Count == 0) return;
        foreach (var file in roots)
            foreach (var declaration in file.Types.OrEmpty())
                if (declaration.Name.OrDefault() is string name && _pendingCaptureRefusals.TryGetValue(name,out var message))
                    throw new NotSupportedException(message);
    }
}
