package metadata

internal class ArrayClone(value: CharArray) : System.ICloneable by value
internal class StringOrder : System.Collections.Generic.IComparer<String> {
    override fun Compare(x: String?, y: String?): Int = 0
}
internal class DualOrder : System.Collections.IComparer, System.Collections.Generic.IComparer<Any> {
    override fun Compare(x: Any?, y: Any?): Int = 0
}
internal class DynamicCast : System.Runtime.InteropServices.IDynamicInterfaceCastable {
    override fun IsInterfaceImplemented(interfaceType: System.RuntimeTypeHandle, throwIfNotImplemented: Boolean): Boolean = false
    override fun GetInterfaceImplementation(interfaceType: System.RuntimeTypeHandle): System.RuntimeTypeHandle = interfaceType
}
