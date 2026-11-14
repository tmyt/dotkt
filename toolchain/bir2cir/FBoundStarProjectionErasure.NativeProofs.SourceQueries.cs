using DotKt.Bir;
using DotKt.Bir.Lowering;
using DeclarationBinding = CurrentDeclarationBindings.DeclarationBinding;

static partial class FBoundStarProjectionErasure
{
    internal sealed partial class NativeProofPlan
    {
        internal DeclarationBinding LocalSourceMethodBinding(string identity)
        {
            if (identity == null || !methods.TryGetValue(identity, out var binding)) return null;
            _ = binding.Source;
            return binding;
        }

        internal BirFunctionDeclaration LocalSourceMethod(string identity) =>
            LocalSourceMethodBinding(identity)?.Source as BirFunctionDeclaration;

        internal bool ExactMethodArgument(DeclarationBinding declaration, string identity, int index) =>
            (declaration != null ? Rows(declaration) : MethodRows(identity)).ContainsKey(index);

        internal bool IsPhysicalConstraint(DeclarationBinding declaration, int index, TypeNode bound) =>
            IsRequiredPhysicalConstraint(Rows(declaration), index, bound);

        internal bool IsPhysicalConstraint(BirFunctionDeclaration declaration, int index, TypeNode bound) =>
            IsRequiredPhysicalConstraint(Rows(bindings.GetCapturedSource(declaration)), index, bound);

        bool IsRequiredPhysicalConstraint(IReadOnlyDictionary<int, List<TypeNode>> required, int index, TypeNode bound) =>
            required.TryGetValue(index, out var proofs) && proofs.Any(proof =>
                references.PhysicalConstraintType(proof) == references.PhysicalConstraintType(bound));
    }
}
