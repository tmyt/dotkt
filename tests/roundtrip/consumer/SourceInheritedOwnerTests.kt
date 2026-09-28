package roundtrip.sourceowner

import NUnit.Framework.TestAttribute

private fun <T> readDerived(value: Derived<Int, T>): T? = value.cell.value
private fun <T> readContract(value: ContractDerived<Int, T>): T? = value.box.value

class SourceInheritedOwnerTests {
    @TestAttribute
    fun inheritedOwnersPreserveSourceArguments() {
        check(Derived<String, Int>("tag", 17).cell.value == 17)
        check(Derived<Int, String>(1, "value").cell.value == "value")
        check(NullableDerived(null).cell.value == null)
        check(NullableDerived(23).cell.value == 23)
    }

    @TestAttribute
    fun genericReceiversPreserveInheritedClassAndInterfaceArguments() {
        check(readDerived(Derived<Int, String>(1, "generic")) == "generic")
        check(readDerived(Derived<Int, Int>(1, 31)) == 31)
        check(readContract(ContractDerived<Int, String>(1, "interface")) == "interface")
        check(readContract(ContractDerived<Int, Int>(1, 37)) == 37)
    }
}
