package roundtrip.localjoin

open class LocalJoinBase {
    protected interface Marker
}

class LocalJoinHost<A>(val seed: A) : LocalJoinBase() {
    fun value(first: Boolean): Any {
        class Local<T>(val item: T) : Marker { fun captured(): A = seed }
        val number = Local(12)
        val text = Local("text")
        val selected = if (first) number else text
        val expected: Any = if (first) number as Any else text as Any
        check(selected === expected && selected.captured() == seed)
        check(selected::class == Local::class && selected::class.simpleName == "Local")
        check(selected::class.qualifiedName == null)
        return selected.item
    }
}
