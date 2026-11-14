using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class FBoundStarProjectionErasure
{
    // Invoked at entry to each owning TypeDef, after its pre-projection frame
    // has been captured and before its body is rewritten. Do not hoist this to
    // module preparation: native proof queries consume the live constraints.
    internal static void ProjectDependentTypeConstraintsSource(IBirTypeDeclaration declaration,
        CarrierRealizationContext context)
    {
        var frame = declaration.KotlinSupertypesPre.OrDefault()?.NullableFrame;
        var parameters = declaration.TypeParams.OrEmpty();
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            if (!parameter.Constraints.HasValue) continue;
            parameter.Constraints = parameter.Constraints.Value.Select(bound =>
                OwnerConstrainedMethodLowering.HasConstructedDependency(bound, "type", index, frame, context.References)
                    && PhysicalBoundClassifier(bound, context.Owners, context.References) is { } classifier
                        ? classifier : bound).ToArray();
            var proofs = context.NativeProofs.Bounds(
                context.Bindings.GetCapturedSource((BirGraphValue)declaration), index);
            parameter.Constraints = parameter.Constraints.Value.Concat(proofs.Select(BirGraphValue.CopyType)).Distinct().ToArray();
        }
    }
}
