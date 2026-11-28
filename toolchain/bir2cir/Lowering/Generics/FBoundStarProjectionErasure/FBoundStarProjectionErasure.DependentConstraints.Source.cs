using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    // Companion bounds describe the value representation, not the Kotlin
    // type variable passed to a reference-constrained native construction.
    static bool IsRepresentationCompanion(NullableRepresentationFrame frame, int index) =>
        frame != null && index < frame.PhysicalArity
        && frame.PhysicalSlot(index).Representation != NullableRepresentationFrame.Role.Ordinary;

    // Invoked at entry to each owning TypeDef, after its pre-projection frame
    // has been captured and before its body is rewritten. Do not hoist this to
    // module preparation: native proof queries consume the live constraints.
    internal static void ProjectDependentTypeConstraintsSource(IBirTypeDeclaration declaration,
        CarrierProjectionContext context)
    {
        var frame = NullableRepresentationViews.TypeFacts((BirGraphValue)declaration)?.NullableFrame;
        var parameters = declaration.TypeParams.OrEmpty();
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            if (!parameter.Constraints.HasValue) continue;
            parameter.Constraints = parameter.Constraints.Value.Select(bound =>
                OwnerConstrainedMethodLowering.HasConstructedDependency(bound, "type", index, frame, context.References)
                    && PhysicalBoundClassifier(bound, context) is { } classifier
                        ? IsRepresentationCompanion(frame, index) ? classifier
                            : context.PreserveReferenceBound(parameter, bound, classifier) : bound).ToArray();
            var proofs = context.NativeProofs.Bounds(
                context.Preparation.Bindings.GetCapturedSource((BirGraphValue)declaration), index);
            parameter.Constraints = parameter.Constraints.Value.Concat(proofs.Select(BirGraphValue.CopyType)).Distinct().ToArray();
        }
    }
}
