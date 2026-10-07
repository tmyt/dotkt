package aliasdescriptor

import NUnit.Framework.TestAttribute
import Interop.AliasDescriptors.Box

private interface Slot<T> : Comparable<Slot<T>> {
    override fun compareTo(other: Slot<T>): Int = 83
}
private open class Base<T> {
    fun CompareTo(other: Slot<List<T>>): Int = 84
}
private class Item<T> : Base<T>(), Slot<List<T>>
private class Rank<T>(val score: Int) : Comparable<Rank<T>> {
    override fun compareTo(other: Rank<T>): Int = score.compareTo(other.score)
}
private fun <T : Comparable<T>> order(first: T, second: T): Int = first.compareTo(second)
private fun <T> direct(first: Slot<T>, second: Slot<T>): Int = first.compareTo(second)
private fun <A, B> directNested(first: Slot<List<B>>, second: Slot<List<B>>, marker: A): Int =
    first.compareTo(second)

class AliasMemberDescriptorTests {
    @TestAttribute
    fun projectedOwnerArgumentsKeepTheSelectedClrDeclaration() {
        val item = Item<String>()
        check((item as Slot<List<String>>).compareTo(item) == 83)
        check(item.CompareTo(item) == 84)
        check(direct<List<String>>(item, item) == 83)
        check(directNested<Int, String>(item, item, 42) == 83)
        val intItem = Item<Int>()
        check(directNested<String, Int>(intItem, intItem, "tag") == 83)
        check(intItem.CompareTo(intItem) == 84)
        check(order(Rank<String>(1), Rank<String>(2)) < 0)
        check(order(Rank<Int>(2), Rank<Int>(1)) > 0)

        val fixedValues = System.Collections.Generic.List<String>()
        fixedValues.Add("fixed")
        val rank = Rank<String>(5)
        val box = Box<Rank<String>>()
        check(box.Keep(rank, fixedValues) === rank)
        check(box.Keep("tag", fixedValues) == 99)
        check(box.Echo<Int>(rank, 42) == 42)
    }
}
