import RawIteratorInaccessibleSuper.RawBase

class Derived : RawBase() {
    override fun iterator(): Iterator<Any?> = super.iterator()
}
