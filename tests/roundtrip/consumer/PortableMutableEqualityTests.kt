import NUnit.Framework.TestAttribute
import roundtrip.mutablepolicy.PortableMutableList
import roundtrip.mutablepolicy.PortableMutableSet
import roundtrip.mutablepolicy.PortableMutableKey
import roundtrip.mutablepolicy.portableMutableEquals
import roundtrip.mutablepolicy.portableMutableHash

private fun portableOrderedHash(values: List<*>): Int {
    var hash = 1
    for (element in values) hash = 31 * hash + (element?.hashCode() ?: 0)
    return hash
}

private fun portableUnorderedHash(values: List<*>): Int {
    var hash = 0
    for (element in values) hash += element?.hashCode() ?: 0
    return hash
}

private fun <T> portableMutableListChecks(values: List<T>) {
    val source = PortableMutableList(values.toMutableList())
    check(source.equals(values) && values.equals(source))
    check(portableMutableEquals(source, values))
    check(source.hashCode() == portableOrderedHash(values))
    check(portableMutableHash(source) == portableOrderedHash(values))
    val opaque: Any = source
    check(opaque == values && opaque.hashCode() == values.hashCode())
}

class CrossModulePortableMutableEqualityTests {
    @TestAttribute fun listsPreserveNullableValueReferenceAndFloatingEquality() {
        portableMutableListChecks(emptyList<Int>())
        portableMutableListChecks(listOf(1, 2, 1))
        portableMutableListChecks(listOf<Int?>(null, 1, null))
        portableMutableListChecks(listOf<String?>("first", null, "last"))
        portableMutableListChecks(listOf(Double.NaN, -0.0, 0.0))
        portableMutableListChecks(listOf(PortableMutableKey(2), PortableMutableKey(3)))
        val keys = PortableMutableList(mutableListOf(PortableMutableKey(2), PortableMutableKey(3)))
        val equalKeys = listOf(PortableMutableKey(2), PortableMutableKey(3))
        check(keys[0] !== equalKeys[0])
        check(keys.equals(equalKeys) && equalKeys.equals(keys))
        check(keys.hashCode() == equalKeys.hashCode())
        val floating = PortableMutableList(mutableListOf(Double.NaN, -0.0))
        check(floating.equals(listOf(Double.NaN, -0.0)))
        check(!floating.equals(listOf(Double.NaN, 0.0)))
    }

    @TestAttribute fun listEqualityRejectsWrongKindsOrderSizeAndElementTypes() {
        val source = PortableMutableList(mutableListOf(1, 2))
        check(!source.equals(null) && !source.equals(Any()))
        check(!source.equals(setOf(1, 2)) && !source.equals(mapOf(1 to 2)))
        check(!source.equals(arrayOf(1, 2)))
        check(!source.equals(listOf(1)) && !source.equals(listOf(2, 1)))
        check(!source.equals(listOf("1", "2")) && !source.equals(listOf<Int?>(1, null)))
        val peer = System.Collections.Generic.List<Int>()
        peer.Add(1); peer.Add(2)
        check(source.equals(peer) && portableMutableEquals(source, peer))
        val rawPeer = System.Collections.ArrayList()
        rawPeer.Add(1); rawPeer.Add(2)
        check(source.equals(rawPeer) && portableMutableEquals(source, rawPeer))
        rawPeer[1] = "2"
        check(!source.equals(rawPeer))
        val star: List<*> = source
        check(star == peer && star.hashCode() == portableOrderedHash(listOf(1, 2)))
    }

    @TestAttribute fun setHashIsUnorderedAndRetainsHeterogeneousQuerySafety() {
        val values = listOf<Any?>(null, 7, "value", PortableMutableKey(2))
        val first = PortableMutableSet(values.toMutableList())
        val second = PortableMutableSet(values.reversed().toMutableList())
        val expected = portableUnorderedHash(values)
        check(first.hashCode() == expected && second.hashCode() == expected)
        check(portableMutableHash(first) == expected)
        check(first.equals(second) && second.equals(first))
        val ints = PortableMutableSet(mutableListOf(1))
        check(!ints.equals(setOf("other")) && !ints.equals(setOf<Int?>(null)))
        check(PortableMutableSet(mutableListOf<Int>()).hashCode() == 0)
    }

    @TestAttribute fun nestedAndNativeValuesKeepKotlinEqualityAndHashes() {
        val leftNative = System.Collections.Generic.List<Int>()
        leftNative.Add(1); leftNative.Add(2)
        val rightNative = System.Collections.Generic.List<Int>()
        rightNative.Add(1); rightNative.Add(2)
        val left = PortableMutableList(mutableListOf<Any?>(leftNative, setOf(3, 4), mapOf("k" to null), null))
        val right = listOf<Any?>(rightNative, setOf(4, 3), mapOf("k" to null), null)
        check(left.equals(right) && right.equals(left))
        check(left.hashCode() == portableOrderedHash(right))
        check(portableMutableHash(left) == right.hashCode())
        val values = listOf<Any?>(leftNative, setOf(3, 4), mapOf("k" to null), null)
        val set = PortableMutableSet(values.toMutableList())
        check(set.hashCode() == portableUnorderedHash(values))
    }

    @TestAttribute fun callableReferencesAndOpaqueCallsRetainAuthoredIteration() {
        val source = PortableMutableList(mutableListOf(1, 2, 3))
        val equal: (Any?) -> Boolean = source::equals
        val hash: () -> Int = source::hashCode
        check(equal(source) && !equal(null) && !equal(Any()))
        check(source.iterations == 0)
        check(equal(listOf(1, 2, 3)) && source.iterations > 0)
        val before = source.iterations
        check(hash() == portableOrderedHash(listOf(1, 2, 3)) && source.iterations > before)
        val set = PortableMutableSet(mutableListOf(1, 2, 3))
        val setHash: () -> Int = set::hashCode
        check(setHash() == 6 && set.iterations > 0)
    }

    @TestAttribute fun mutationsAndSubListsRetainCrossModuleValueSemantics() {
        val source = PortableMutableList(mutableListOf<Int?>(null, 1, 2))
        val mutable: MutableList<Int?> = source
        mutable[1] = 7
        mutable.add(3)
        mutable.removeAt(0)
        val expected = listOf<Int?>(7, 2, 3)
        check(source.equals(expected) && portableMutableEquals(source, expected))
        check(source.hashCode() == portableOrderedHash(expected))
        val view = mutable.subList(1, 3)
        check(view.equals(listOf<Int?>(2, 3)) && view.hashCode() == portableOrderedHash(listOf<Int?>(2, 3)))
        mutable.clear()
        check(source.equals(emptyList<Int?>()) && source.hashCode() == 1)
        val set = PortableMutableSet(mutableListOf<Int?>(null, 1))
        val mutableSet: MutableSet<Int?> = set
        mutableSet.add(7); mutableSet.remove(1)
        check(portableMutableHash(set) == 7)
        mutableSet.clear()
        check(set.hashCode() == 0)
    }
}
