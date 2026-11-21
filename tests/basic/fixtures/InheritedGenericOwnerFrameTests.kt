package inheritedgenericownerframe

import NUnit.Framework.TestAttribute

class Box<out T>

private open class Base<K, E> {
    fun demand(x: Box<K?>) {}
    fun ready(): Boolean = true
}

private class Derived<K> : Base<K, Box<K>>() {
    fun viaInherited(): Boolean = ready()
}

private class Rank<T>(val number: Int) : Comparable<Rank<*>> {
    override fun compareTo(other: Rank<*>): Int = number.compareTo(other.number)
}

class InheritedGenericOwnerFrameTests {
    @TestAttribute
    fun inheritedCallUsesThePhysicalOwnerWithoutRewritingItsSourceOverride() {
        check(Derived<Int>().viaInherited())
    }

    @TestAttribute
    fun comparableBridgeUsesTheProjectedParameterNotTheSourceMarker() {
        val higher = Rank<Int>(2)
        val lower = Rank<String>(1)
        val boxed: Any = higher
        check(boxed is System.IComparable)
        check(listOf(higher, lower).sorted().first() === lower)
    }
}
