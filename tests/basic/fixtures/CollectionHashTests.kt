import NUnit.Framework.TestAttribute

private fun <T> collectionGenericHash(value: T): Int = value?.hashCode() ?: 0
private fun <T> collectionNullableSet(value: T?): HashSet<T?> = hashSetOf(value)
private fun sameCollectionHash(left: Any, right: Any) {
    check(left == right)
    check(left.hashCode() == right.hashCode())
    check(collectionGenericHash(left) == collectionGenericHash(right))
}

private open class CollectionHashBase { override fun hashCode(): Int = 19 }
private class CollectionHashDerived : CollectionHashBase() { override fun hashCode(): Int = super.hashCode() + 1 }
private class CollectionObservedHash {
    var calls = 0
    override fun hashCode(): Int { calls++; return 211 }
}
private var collectionHashTrace = ""
private class CollectionHashKey(val id: Int) {
    override fun hashCode(): Int { collectionHashTrace += "h$id;"; return id }
    override fun equals(other: Any?): Boolean = other is CollectionHashKey && other.id == id
}
private fun collectionHashKey(id: Int): CollectionHashKey {
    collectionHashTrace += "a$id;"
    return CollectionHashKey(id)
}

class CollectionHashTests {
    @TestAttribute
    fun nativeAliasHashesFollowTheirKotlinEqualityFamilies() {
        val first = listOf(23, 42)
        val second = listOf(23, 42)
        check(first.hashCode() == second.hashCode())
        sameCollectionHash(first, second)
        sameCollectionHash(mapOf(1 to "a", 2 to "b"), mapOf(2 to "b", 1 to "a"))
        sameCollectionHash(setOf(1, 2), setOf(2, 1))
        check(arrayListOf(23, 42).hashCode() == arrayListOf(23, 42).hashCode())
        val map = HashMap<Int, String>()
        map[1] = "a"
        check(map.hashCode() == hashMapOf(1 to "a").hashCode())
        val linked = LinkedHashMap<Int, String>()
        linked[1] = "a"
        check(linked.hashCode() == map.hashCode())
    }

    @TestAttribute
    fun hashMapsUseKotlinKeysForEveryConstructorShape() {
        val key = listOf(23, 42)
        val maps = arrayOf(HashMap<List<Int>, String>(), HashMap<List<Int>, String>(8), HashMap<List<Int>, String>(8, 0.75f))
        for (map in maps) {
            map[key] = "stored"
            check(map[listOf(23, 42)] == "stored")
            check(HashMap(map)[listOf(23, 42)] == "stored")
            check(LinkedHashMap(map)[listOf(23, 42)] == "stored")
            check(map.remove(listOf(23, 42)) == "stored")
        }
        val ordered = LinkedHashMap<List<Int>, String>(8, 0.75f)
        ordered[key] = "ordered"
        check(ordered[listOf(23, 42)] == "ordered")
    }

    @TestAttribute
    fun hashSetsUseKotlinElementsForEveryConstructorShape() {
        val sets = arrayOf(HashSet<List<Int>>(), HashSet<List<Int>>(8), HashSet<List<Int>>(8, 0.75f))
        for (set in sets) {
            check(set.add(listOf(23, 42)))
            check(!set.add(listOf(23, 42)))
            check(set.contains(listOf(23, 42)))
            check(HashSet(set).contains(listOf(23, 42)))
            check(set.remove(listOf(23, 42)))
            check(set.isEmpty())
        }
    }

    @TestAttribute
    fun literalAndForwardedFactoriesUseTheSameComparer() {
        val first = listOf(23, 42)
        val second = listOf(23, 42)
        val literal = mapOf(first to "first", second to "second")
        check(literal.size == 1 && literal[second] == "second")
        check(setOf(first, second).size == 1)
        val pairs = arrayOf(first to "first", second to "second")
        check(mapOf(*pairs).size == 1)
        check(setOf(*arrayOf(first, second)).size == 1)
        val ordered = linkedSetOf(first, second)
        check(ordered.size == 1 && ordered.contains(listOf(23, 42)))
        collectionHashTrace = ""
        check(mapOf(collectionHashKey(1) to 1, collectionHashKey(2) to 2).size == 2)
        check(collectionHashTrace.startsWith("a1;a2;")) { "map argument/hash order: $collectionHashTrace" }
        collectionHashTrace = ""
        check(setOf(collectionHashKey(1), collectionHashKey(2)).size == 2)
        check(collectionHashTrace.startsWith("a1;a2;")) { "set argument/hash order: $collectionHashTrace" }
    }

    @TestAttribute
    fun nestedCollectionsUseStructuralHashesRecursively() {
        val first: Any = listOf(mapOf(1 to setOf(23, 42)), null)
        val second: Any = listOf(mapOf(1 to setOf(42, 23)), null)
        sameCollectionHash(first, second)
        val map = hashMapOf(first to "nested")
        check(map[second] == "nested")
        val shared = arrayOf(1)
        sameCollectionHash(listOf(shared), listOf(shared))
        check(listOf(arrayOf(1)) != listOf(arrayOf(1)))
        val changing = mutableListOf(1)
        sameCollectionHash(changing, listOf(1))
        changing.add(2)
        sameCollectionHash(changing, listOf(1, 2))
        val keys = listOf(listOf(1), listOf(1), listOf(2))
        check(keys.distinct().size == 2)
        check(keys.toSet().size == 2)
        check(keys.groupBy { it }.size == 2)
        check(setOf(listOf(1)).intersect(setOf(listOf(1))).size == 1)
    }

    @TestAttribute
    fun authoredHashOverridesAndNonvirtualSuperCallsStayObservable() {
        val value = CollectionObservedHash()
        check(value.hashCode() == 211 && value.calls == 1)
        val opaque: Any = value
        check(opaque.hashCode() == 211 && value.calls == 2)
        val bound: () -> Int = value::hashCode
        check(bound() == 211 && value.calls == 3)
        check(CollectionHashDerived().hashCode() == 20)
    }

    @TestAttribute
    fun boundAndUnboundHashReferencesUseKotlinHashing() {
        val first = listOf(23, 42)
        val second: Any = listOf(23, 42)
        val bound: () -> Int = first::hashCode
        val opaqueBound: () -> Int = second::hashCode
        val unbound: (Any) -> Int = Any::hashCode
        check(bound() == opaqueBound())
        check(bound() == unbound(second))
        check(bound() == first.hashCode())
        val changing = mutableListOf(1)
        val read: () -> Int = changing::hashCode
        changing.add(2)
        check(read() == listOf(1, 2).hashCode())
    }

    @TestAttribute
    fun scalarNullAndFloatingPointKeySemanticsRemainKotlinSemantics() {
        val absent: Any? = null
        check(absent.hashCode() == 0)
        check(collectionGenericHash(23) == 23.hashCode())
        check(collectionGenericHash("value") == "value".hashCode())
        check(hashSetOf(Double.NaN, Double.NaN).size == 1)
        check(hashSetOf(0.0, -0.0).size == 2)
        check(hashSetOf<Any?>(null, null).size == 1)
        val nullable = collectionNullableSet<Int>(null)
        nullable.add(1)
        check(nullable.contains(null) && nullable.contains(1) && nullable.size == 2)
        check(HashSet(nullable).size == 2)
    }
}
