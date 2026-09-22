import NUnit.Framework.TestAttribute

private class LocalFactoryFrameBox<T>(val value: T)
private class LocalFactoryFrameOwner<T>(private val seed: T) {
    fun boxed(): LocalFactoryFrameBox<List<T>> = LocalFactoryFrameBox(listOf(seed))
}
private fun <T> localFactoryFrameBox(value: T): LocalFactoryFrameBox<List<T>> = LocalFactoryFrameBox(listOf(value))
private class LocalFactoryWideList(val values: List<Any?>)
private class LocalFactoryWideMap(val values: Map<String, Any?>)

class CollectionFactoryConstructorTests {
    @TestAttribute fun sameModuleOwnerGenericFactoryKeepsItsElement() {
        check(LocalFactoryFrameOwner(7).boxed().value[0] == 7)
        check(LocalFactoryFrameOwner("a").boxed().value[0] == "a")
    }

    @TestAttribute fun methodGenericFactoryKeepsItsElement() {
        check(localFactoryFrameBox(7).value[0] == 7)
        check(localFactoryFrameBox("a").value[0] == "a")
    }

    @TestAttribute fun constructorContextStillWidensFreshCollectionFactories() {
        val list = LocalFactoryWideList(listOf(7))
        check(list.values.size == 1 && list.values[0] == 7)
        val map = LocalFactoryWideMap(mapOf("value" to 7))
        check(map.values["value"] == 7)
    }
}
