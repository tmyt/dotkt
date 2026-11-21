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

class InheritedGenericOwnerFrameTests {
    @TestAttribute
    fun inheritedCallUsesThePhysicalOwnerWithoutRewritingItsSourceOverride() {
        check(Derived<Int>().viaInherited())
    }
}
