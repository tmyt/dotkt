package roundtrip.sourceowner

import NUnit.Framework.TestAttribute

private fun <T> readDerived(value: Derived<Int, T>): T? = value.cell.value
private fun <T> readContract(value: ContractDerived<Int, T>): T? = value.box.value

private class InheritedPair<K, V>(val first: K, val second: V)
private interface InheritedPairContract<T> { fun pass(value: T): T }
private open class InheritedPairBase<T> { fun pass(value: T): T = value }
private class InheritedPairForwarder<K, V> :
    InheritedPairBase<InheritedPair<K, V>>(), InheritedPairContract<InheritedPair<K, V>>
private fun <K, V> passInheritedPair(value: InheritedPair<K, V>): InheritedPair<K, V> {
    val contract: InheritedPairContract<InheritedPair<K, V>> = InheritedPairForwarder<K, V>()
    return contract.pass(value)
}

class SourceInheritedOwnerTests {
    @TestAttribute
    fun inheritedOwnersPreserveSourceArguments() {
        check(Derived<String, Int>("tag", 17).cell.value == 17)
        check(Derived<Int, String>(1, "value").cell.value == "value")
        check(NullableDerived(null).cell.value == null)
        check(NullableDerived(23).cell.value == 23)
        val nullable = NullableLayer<Int?>(41)
        val empty = NullableLayer<Int?>(null)
        val reference = NullableLayer<String?>("nullable")
        check(nullable.cell.value == 41)
        check(empty.cell.value == null)
        check(reference.cell.value == "nullable")
    }

    @TestAttribute
    fun genericReceiversPreserveInheritedClassAndInterfaceArguments() {
        val pair = InheritedPair(17, "nested")
        check(passInheritedPair(pair) === pair)
        check(passInheritedPair(pair).first == 17 && passInheritedPair(pair).second == "nested")
        val nullablePair = InheritedPair<Int?, String?>(null, null)
        check(passInheritedPair(nullablePair) === nullablePair)
        val leaf: Leaf = LeafImpl(43)
        check(leaf.item.value == 43)
        val defaultLeaf: DefaultLeaf = DefaultLeafImpl()
        check(defaultLeaf.answer == 47)
        check(readDerived(Derived<Int, String>(1, "generic")) == "generic")
        check(readDerived(Derived<Int, Int>(1, 31)) == 31)
        check(readContract(ContractDerived<Int, String>(1, "interface")) == "interface")
        check(readContract(ContractDerived<Int, Int>(1, 37)) == 37)
    }
}
