package roundtriptests.genericindexing

import NUnit.Framework.TestAttribute
import roundtrip.genericindexing.*

class OverrideIndex : Index() {
    var gets: Int = 0
    var sets: Int = 0
    override fun <T> get(value: T): T {
        gets++
        return super.get(value)
    }
    override fun <T> set(index: Int, value: T) {
        sets++
        super.set(index, value)
    }
}

private fun <O, T> checkGenericIndex(owner: O, value: T) {
    val index = OwnerIndex(owner)
    check(index[value] == value)
    index[owner] = value
    check(index.last == value)
}

class GenericIndexingOperatorTests {
    @TestAttribute
    fun getAndSetRetainResolvedReferenceValueAndNullableArguments() {
        val index = Index()
        check(index["value"] == "value")
        check(index[42] == 42)
        check(index[null as String?] == null)
        check(index.get<Int?>(null) == null)
        index[0] = "stored"
        check(index.last == "stored")
        index[1] = 43
        check(index.last == 43)
        index[2] = null as Int?
        check(index.last == null)
        index.set<String?>(3, null)
        check(index.last == null)
    }

    @TestAttribute
    fun methodArgumentsStayIndependentFromOwnerAndCallerFrames() {
        checkGenericIndex("key", 47)
        checkGenericIndex(53, "value")
        checkGenericIndex<String?, Int?>(null, 59)
        checkGenericIndex<Int?, String?>(61, null)
        checkGenericIndex(listOf(1), listOf("nested"))
    }

    @TestAttribute
    fun inheritedOverloadVirtualAndSuperDispatchRemainSelected() {
        val derived = DerivedIndex()
        check(derived[67] == 67)
        check(derived[71, "overload"] == "71:overload")
        val child = OverrideIndex()
        val base: Index = child
        check(base["virtual"] == "virtual")
        base[0] = 73
        check(child.last == 73 && child.gets == 1 && child.sets == 1)
    }

    @TestAttribute
    fun boundedLookupAndOrdinaryIndexersRetainTheirContracts() {
        val element = Element(79)
        val key = Key(element)
        val lookup = Lookup()
        check(lookup[key] === element)
        lookup[key] = element
        check(lookup.last === element)
        lookup[key] = null
        check(lookup.last == null)
        val plain = PlainIndex()
        check(plain[2] == 3)
        plain[3] = 7
        check(plain.last == 10)
    }
}
