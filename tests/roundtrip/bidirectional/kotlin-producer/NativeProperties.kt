import BidirectionalInterop.NativePropertyBase

class ExportedNativeProperties : NativePropertyBase() {
    override var Item: System.IComparable = System.Version(1, 0) as System.IComparable
    override val Caption: String get() = "override"
    protected override val ProtectedValue: Int get() = 42
}

private var staticPropertyStorage: Int = 17
var exportedStaticProperty: Int
    get() = staticPropertyStorage
    set(value) { staticPropertyStorage = value }
