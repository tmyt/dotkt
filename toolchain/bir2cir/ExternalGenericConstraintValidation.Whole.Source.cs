using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class ExternalGenericConstraintValidation
{
    public sealed partial class Prepared
    {
        // This is whole use-site validation, not selected-method validation.
        // Preparation, local facts and the referenced declaration cache remain
        // the same objects across every file and the later resolved-member pass.
        public void Apply(BirGraphValue root) => WalkSourceTypes(root, null,
            Array.Empty<ParameterFacts>(), Array.Empty<ParameterFacts>());

        void WalkSourceTypes(BirGraphValue node, string incomingField,
            ParameterFacts[] typeParameters, ParameterFacts[] methodParameters)
        {
            if (node is null or BirMemberReference or IBirExecutablePayload) return;
            var nextTypes = typeParameters;
            var nextMethods = methodParameters;
            if (node is IBirTypeDeclaration owner && IsTypeKind(owner.Kind.OrDefault()))
                nextTypes = ReadSourceParameterFacts(TypeParameterFrame.Declarations(owner));
            if (incomingField == "methods")
                nextMethods = ReadSourceParameterFacts(node is BirFunctionDeclaration method ? method.TypeParams.OrEmpty() : []);
            else if (incomingField == "ctors") nextMethods = Array.Empty<ParameterFacts>();

            bool Included(string field) => !FBoundStarProjectionErasure.IsTransientMarker(field)
                && UseSiteTypeField(field, node is BirNew);
            // Validation is read-only. Generated structural readers include
            // wire TypeSpecs, not frozen text/bytes or another declaration's
            // opaque source carriers; no role-wide exclusion loses live types.
            node.VisitOwnTypeFields((field, type) => {
                if (Included(field)) ValidateType(type, nextTypes, nextMethods, _refs, _isValueFqn,
                    _localEnums, _localTypes, _declarationCache);
            });
            node.VisitOwnChildSlots((field, _, slot) => {
                if (Included(field)) WalkSourceTypes(slot.Value, field, nextTypes, nextMethods);
            });
        }
    }
}
