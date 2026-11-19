package metadata

internal class ArrayClone(value: CharArray) : System.ICloneable by value
internal class StringOrder : System.Collections.Generic.IComparer<String> {
    override fun Compare(x: String?, y: String?): Int = 0
}
