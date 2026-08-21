import DeclarationIdentityInterop.StorageCollision1

// The generic arm is genuinely byref-like and remains illegal in the suspend ABI after the same-stem index is split.
suspend fun sameStemGenericByRefLike(value: StorageCollision1<Int>): Int = value.Value
