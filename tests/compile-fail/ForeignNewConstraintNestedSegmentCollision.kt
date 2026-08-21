import DeclarationIdentityInterop.Constraints
import DeclarationIdentityInterop.ConstructorSegmentOuter.Leaf as PrivateCtorLeaf

fun main() {
    // The sibling struct has the same flattened owner and total arity, but a different per-segment CLR identity.
    Constraints.NeedsNew<PrivateCtorLeaf<Int, String>>()
}
