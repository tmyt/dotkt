import BidirectionalInterop.NativePropertyBase

class ExportedNativeProperties : NativePropertyBase() {
    override var Item: System.IComparable = "initial" as System.IComparable
    override val Caption: String get() = "override"
    protected override val ProtectedValue: Int get() = 42
}

var exportedStaticProperty: Int = 17
