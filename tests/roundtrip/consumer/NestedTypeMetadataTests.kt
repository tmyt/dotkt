import NUnit.Framework.TestAttribute
import nestedmetadata.*

fun forwardedNestedMetadataItem(item: Outer<*>.Item<String>): String = readInner(item)
fun forwardedNestedMetadataLeaf(leaf: Nest<*>.Middle<String>.Leaf<Int>): Int = readLeaf(leaf)

class NestedTypeMetadataTests {
    @TestAttribute
    fun projectedInnerSlotsRetainTheirClassifierAndArgumentOrder() {
        val item = Outer(19).Item("inner")
        check(readInner(item) == "inner")
        check(readOuter(item) == 19)
        check(readNullable(item) == "inner")
        check(readNullable(null) == null)
        check(echoInner(item) === item)
        check(echoInner(item).value == "inner")
    }

    @TestAttribute
    fun multipleEnclosingOwnersRetainDistinctTypeArguments() {
        val leaf = Nest(true).Middle("mid").Leaf(7)
        check(readLeaf(leaf) == 10)
        check(readLeafOuter(leaf))
    }
}
