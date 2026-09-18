package roundtriptests.nominallist

import NUnit.Framework.TestAttribute
import roundtrip.nominallist.*

private class ImportedNominalList : NominalMutableList<Int>(mutableListOf(7, 9))

class NominalListIdentityTests {
    @TestAttribute fun importedMutableListRetainsClassifierAndMembers() {
        for (value in arrayOf<Any>(nominalMutableValue(), ImportedNominalList())) {
            check(value is List<*> && value is MutableList<*>)
            check(nominalListIs<List<*>>(value))
            check(nominalListSafe<MutableList<*>>(value) === value)
            val list = value as MutableList<*>
            check(list.size == 2 && list[0] == 7)
            check(list.removeAt(0) == 7)
            list.clear()
            check(list.isEmpty())
        }
    }

    @TestAttribute fun importedReadOnlyListDoesNotGainMutability() {
        val value = nominalReadOnlyValue()
        check(value is List<*> && value !is MutableList<*>)
        check(nominalListSafe<List<*>>(value) === value)
        check(nominalListSafe<MutableList<*>>(value) == null)
        check((value as List<*>)[1] == 9)
    }

    @TestAttribute fun projectedReturnCrossesAssemblyWithoutWrappingIdentity() {
        val value = nominalMutableValue()
        val list = nominalListCast(value)
        check(list === value && list.size == 2 && list[0] == 7)
        val mutable = nominalMutableListCast(value)
        check(mutable === value)
        check(mutable.removeAt(0) == 7)
        check(list.size == 1 && list[0] == 9)
    }
}
