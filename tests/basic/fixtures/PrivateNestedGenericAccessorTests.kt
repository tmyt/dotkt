import NUnit.Framework.TestAttribute

private class PrivateNestedCursorOwner<T>(val value: T) {
    private open inner class Cursor {
        protected var position: Int = 0
        @kotlin.clr.ClrField protected var marker: Int = 0
        protected fun <R : Comparable<R>> echo(value: R): R = value
    }

    private inner class AdvancingCursor : Cursor() {
        fun advance(): Int {
            position += 1
            marker += 1
            check(marker == position)
            check(echo(position) == position)
            val bound: (String) -> String = ::echo
            check(bound("bound") == "bound")
            return position
        }
    }

    fun exercise(): Int {
        val cursor = AdvancingCursor()
        check(cursor.advance() == 1)
        return cursor.advance()
    }
}

class PrivateNestedGenericAccessorTests {
    @TestAttribute
    fun protectedPropertyOnPrivateGenericInnerType() {
        check(PrivateNestedCursorOwner("text").exercise() == 2)
        check(PrivateNestedCursorOwner(7).exercise() == 2)
    }
}
