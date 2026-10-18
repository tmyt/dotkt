interface FrameMarker<Tag>

class EnumCallableFrame<Tag, T> where T : Enum<T>, T : FrameMarker<Tag> {
    fun bound(left: T, right: T): Int {
        val operation = left::compareTo
        return operation(right)
    }

    fun unbound(left: T, right: T): Int {
        val operation = Enum<T>::compareTo
        return operation(left, right)
    }
}
