import NUnit.Framework.TestAttribute
import nestedmetadata.*

fun forwardedNestedMetadataItem(item: Outer<*>.Item<String>): String = readInner(item)
fun forwardedNestedMetadataLeaf(leaf: Nest<*>.Middle<String>.Leaf<Int>): Int = readLeaf(leaf)

class NestedTypeMetadataTests {
    @TestAttribute
    fun projectedInnerSlotsRetainTheirClassifierAndArgumentOrder() {
        check(firstNestedText("first") == "first")
        check(secondNestedText("second") == "second")
        val builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<Int>()
        builder.Add(7)
        check(nestedClrBuilderCount(builder) == 1)
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
