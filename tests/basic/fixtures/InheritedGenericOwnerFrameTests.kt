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

open class Payload(val ready: Boolean)
class Leaf<T> : Payload(true)
class Stored<T>(val item: T) {
    fun acceptsNullable(item: T?) = item
}

private open class Parent<T>(val item: T) {
    open fun ready(): Boolean = false
}

private class NestedOverride : Parent<Stored<Leaf<*>>>(Stored(Leaf<Int>())) {
    override fun ready(): Boolean = item.item.ready
}

class InheritedGenericOwnerFrameTests {
    @TestAttribute
    fun inheritedCallUsesThePhysicalOwnerWithoutRewritingItsSourceOverride() {
        check(Derived<Int>().viaInherited())
    }

    @TestAttribute
    fun nestedTypesInAnOverrideMarkerAreNotPhysicalWitnessApplications() {
        val parent: Parent<Stored<Leaf<*>>> = NestedOverride()
        check(parent.ready())
    }
}
