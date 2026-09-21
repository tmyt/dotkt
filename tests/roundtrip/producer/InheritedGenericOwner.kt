package roundtrip.inheritedowner

class ImportedIntOwner : InheritedGenericOwners.IntBridge()
class ImportedGenericOwner<T : Any> : InheritedGenericOwners.GenericBridge<T>() {
    fun forward(value: T): T = Echo(value)
}
class ImportedNestedOwner : InheritedGenericOwners.NestedBridge()
