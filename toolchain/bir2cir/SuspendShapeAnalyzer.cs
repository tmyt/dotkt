using System.Collections.Generic;
using System.Linq;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed class SuspendShapeAnalyzer
{
    public static SuspendShapeAnalysis Analyze(BirFileDeclaration root)
    {
        var count = root.Methods.OrEmpty().Count(method => method.Mods.OrDefault()?.Suspend.OrDefault() == true);
        foreach (var type in root.Types.OrEmpty())
        {
            var methods = type switch {
                BirClassDefinition value => value.Methods.OrEmpty(),
                BirInterfaceDefinition value => value.Methods.OrEmpty(),
                BirEnumDefinition => [],
                _ => throw new System.InvalidOperationException(),
            };
            count += methods.Count(method => method.Mods.OrDefault()?.Suspend.OrDefault() == true);
        }
        return new SuspendShapeAnalysis(count);
    }

}

sealed record SuspendShapeAnalysis(int FunctionCount)
{
    public static SuspendShapeAnalysis Combine(IEnumerable<SuspendShapeAnalysis> analyses) =>
        new(analyses.Sum(a => a.FunctionCount));
}

