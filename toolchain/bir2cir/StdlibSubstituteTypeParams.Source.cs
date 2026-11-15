using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class StdlibSubstituteTypeParams
{
    internal static void Apply(BirGraphValue root)
    {
        void Walk(BirGraphValue node)
        {
            if (node is null or IBirExecutablePayload) return;
            var runtimeInterface = node is IBirTypeDeclaration owner && owner.Kind.OrDefault() == "interface";
            node.VisitOwnChildSlots((field, _, slot) => {
                if (FBoundStarProjectionErasure.IsTransientMarker(field)) return;
                // Captured enclosing parameters are not the declaration's own
                // typeParams vector. The old owner intentionally leaves them.
                if (field == "typeParams" && slot.Value is BirTypeParameter parameter)
                {
                    var declaration = new TypeParameterDeclaration(parameter.Name.OrDefault(),
                        parameter.Reified.HasValue ? parameter.Reified.Value : null,
                        parameter.Variance.OrDefault(), parameter.Constraints.OrDefault(),
                        parameter.ErasedInnerConstraints.OrDefault(), parameter.SpecialConstraints.OrDefault());
                    var physical = Rewrite(declaration, runtimeInterface);
                    parameter.Constraints = physical.Constraints == null ? default : OptionalField<TypeNode[]>.Of(physical.Constraints);
                    parameter.Variance = physical.Variance == null ? default : OptionalField<string>.Of(physical.Variance);
                }
                Walk(slot.Value);
            });
        }
        Walk(root);
    }
}
