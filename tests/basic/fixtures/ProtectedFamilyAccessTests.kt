import NUnit.Framework.TestAttribute
import kotlin.clr.ClrField

private class FamilyOuter<T>(private val payload: T) {
    private open inner class Base {
        protected var index = 3
        @ClrField protected var raw = 11
        protected open fun read(): Int = index + raw
        protected fun transform(block: (Int) -> Int): Int = block(index)
        protected val callback: () -> Int get() = { index }
        fun virtualRead(): Int = read()
    }
    private open inner class Derived : Base() {
        fun ordinary(): Int { index = 7; raw = 13; return index + raw }
        fun aliased(): Int { val self = this; self.index++; self.raw++; return self.index + self.raw }
        fun captured(): () -> Int { val delta = 1; return { index + raw + delta } }
        fun bound(): () -> Int = this::read
        fun <D : Derived> viaBound(other: D): Int = other.index + other.raw
        fun functions(): Int = transform { it + raw } + callback()
        override fun read(): Int = super.read() + 100
    }
    fun run(): T {
        val value = Derived()
        check(value.ordinary() == 20 && value.aliased() == 22)
        check(value.captured()() == 23 && value.viaBound(value) == 22)
        check(value.functions() == 30)
        check(value.virtualRead() == 122 && value.bound()() == 122)
        return payload
    }
}

private class ExistingPrivateFamilyPath<T>(private val value: T) {
    class Reader { fun <U> read(owner: ExistingPrivateFamilyPath<U>): U = owner.value }
}

private class FamilyImmutableList<T>(private val values: List<T>) : AbstractList<T>() {
    override val size: Int get() = values.size
    override fun get(index: Int): T = values[index]
}

private class FamilyMutableList<T>(private val values: MutableList<T>) : AbstractMutableList<T>() {
    override val size: Int get() = values.size
    override fun get(index: Int): T = values[index]
    override fun set(index: Int, element: T): T = values.set(index, element)
    override fun add(index: Int, element: T) { values.add(index, element) }
    override fun removeAt(index: Int): T = values.removeAt(index)
}

private fun <T> checkFamilyIterators(first: T, second: T, third: T) {
    val immutable = FamilyImmutableList(listOf(first, second, third)).listIterator(1)
    check(immutable.hasPrevious() && immutable.previousIndex() == 0 && immutable.nextIndex() == 1)
    check(immutable.previous() == first && immutable.next() == first && immutable.next() == second)
    check(immutable.next() == third && !immutable.hasNext())
    val list = FamilyMutableList(mutableListOf(first, second, third))
    val mutable = list.listIterator(1)
    check(mutable.previous() == first)
    mutable.set(third)
    check(mutable.next() == third)
    mutable.remove()
    mutable.add(first)
    check(mutable.next() == second && mutable.previous() == second)
    mutable.set(first)
    mutable.remove()
    check(list.size == 2 && list[0] == first && list[1] == third)
    check(mutable.previousIndex() == 0 && mutable.nextIndex() == 1)
    check(mutable.previous() == first && mutable.next() == first && mutable.next() == third && !mutable.hasNext())
}

class ProtectedFamilyAccessTests {
    @TestAttribute fun privateGenericNestedReceiversRetainAccessAndDispatch() {
        check(FamilyOuter("reference").run() == "reference")
        check(FamilyOuter(41).run() == 41)
        check(FamilyOuter<Int?>(null).run() == null)
        val reader = ExistingPrivateFamilyPath.Reader()
        check(reader.read(ExistingPrivateFamilyPath(43)) == 43)
        check(reader.read(ExistingPrivateFamilyPath<String?>(null)) == null)
    }

    @TestAttribute fun actualAbstractListIteratorsSupportBidirectionalMutation() {
        checkFamilyIterators("first", "second", "third")
        checkFamilyIterators(3, 5, 7)
        checkFamilyIterators<Int?>(null, 5, 7)
        checkFamilyIterators<String?>(null, "second", "third")
    }
}
