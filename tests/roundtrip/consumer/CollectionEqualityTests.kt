package roundtriptests.collectionequality

import NUnit.Framework.TestAttribute
import roundtrip.collectionequality.*

class CollectionEqualityTests {
    @TestAttribute
    fun stdlibViewsSupplyTheirOwnVirtualDefaults() {
        val source = mutableListOf(10, 23, 42, 99)
        val view: Any = source.subList(1, 3)
        val readonly: Any = (source as List<*>).subList(1, 3)
        check(view == listOf(23, 42) && readonly == listOf(23, 42))
        val map = mutableMapOf(23 to "x", 42 to "y")
        val keys: Any = map.keys
        check(keys == setOf(42, 23))
        val entries: Any = map.entries
        check(entries == mapOf(42 to "y", 23 to "x").entries)
    }

    @TestAttribute
    fun storageAndModuleBoundariesDoNotChangeCollectionEquality() {
        val pairs = arrayOf(
            producerList() to listOf(23, 42),
            producerSet() to setOf(42, 23),
            producerMap() to mapOf(42 to "y", 23 to "x")
        )
        for ((left, right) in pairs) {
            val nullable: Any? = left
            check(left == right && left.equals(right))
            check(nullable == right)
            check(equalInProducer(left, right) && explicitInProducer(left, right))
            check(inlineEqual(left, right) && EqualityStorage(left).sameAs(right))
            val bound = left::equals
            check(bound(right))
            val unbound: (Any, Any?) -> Boolean = Any::equals
            check(unbound(left, right))
        }
    }

    @TestAttribute
    fun authoredEqualityWinsAndInterfacesDoNotInventAnOverride() {
        val plain = DefaultList()
        val plainView: List<Int> = plain
        check(!plain.equals(listOf(23, 42)))
        check(!plainView.equals(listOf(23, 42)))
        val authored = CountingList()
        val any: Any = authored
        val list: List<Int> = authored
        check(any == listOf(23, 42) && authored.calls == 1)
        check(list.equals(listOf(23, 42)) && authored.calls == 2)
        check(list == listOf(23, 42) && authored.calls == 3)
        val reference = list::equals
        check(reference(listOf(23, 42)) && authored.calls == 4)
        check(!SuperDefaultList().equals(listOf(23, 42)))
        val overload = OverloadedEquality()
        check(overload.equals("x"))
        val opaque: Any = overload
        check(!opaque.equals("x"))
    }

    @TestAttribute
    fun identityNullAndOperandEvaluationRemainFaithful() {
        val observed = ObservedEquality()
        val opaque: Any = observed
        check(!(opaque == opaque) && observed.calls == 1)
        check(!opaque.equals(null) && observed.calls == 2)
        val nullable: Any? = opaque
        check(nullable!! === opaque && observed.calls == 2)
        check(nullable != null && observed.calls == 2)
        val array: Any = arrayOf(23)
        val otherArray: Any = arrayOf(23)
        check(array == array && array != otherArray)
        val plain: Any = Any()
        check(plain == plain && plain != Any())
        val missing: Any? = null
        check(missing == null && missing != plain && plain != missing)
        var order = ""
        fun left(): Any { order += "L"; return listOf(23) }
        fun right(): Any { order += "R"; return listOf(23) }
        check(left() == right() && order == "LR")
    }

    @TestAttribute
    fun logicalFamiliesAndNestedElementsAreComparedWithoutGenericCasts() {
        val left: Any = listOf(listOf(23), setOf("x"), mapOf("a" to listOf(42)), null)
        val right: Any = listOf(listOf(23), setOf("x"), mapOf("a" to listOf(42)), null)
        check(left == right)
        check(!left.equals(listOf(listOf(24), setOf("x"), mapOf("a" to listOf(42)), null)))
        val list: Any = listOf(23)
        val set: Any = setOf(23)
        val map: Any = mapOf(23 to 42)
        check(list != set && set != list && list != map && map != list)
        val nullMap: Any = mapOf("x" to null)
        check(nullMap == mapOf("x" to null) && nullMap != mapOf("y" to null))
        val references: Any = arrayListOf("x", "y")
        check(references == listOf("x", "y"))
        val values: Any = hashSetOf(23, 42)
        check(values == setOf(42, 23))
        val floating: Any = listOf(-0.0, Double.NaN)
        check(floating == listOf(-0.0, Double.NaN))
        check(floating != listOf(0.0, Double.NaN))
    }
}
