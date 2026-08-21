import DeclarationIdentityInterop.StorageSegmentOuter1.Leaf as NestedByRefLike

// The generic arm is genuinely byref-like even though a class with the same flattened owner and total arity exists.
suspend fun sameStemGenericByRefLike(value: NestedByRefLike<Int, String>): String = value.Value
