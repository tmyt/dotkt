import NUnit.Framework.TestAttribute

private interface StorageFrameNamed { val name: String }
private class StorageFrameItem(override val name: String) : StorageFrameNamed
private fun <X> storageFrameSink(value: X) {}
private class StorageFrameOwner<T : StorageFrameNamed> {
    var calls: Int = 0
    fun use(value: T) { storageFrameSink(value); calls++ }
}
private fun <T : StorageFrameNamed> makeStorageFrameOwner() { StorageFrameOwner<T>() }

private class StorageFrameCell<T>(val value: T)
private class StorageFrameHolder<T : StorageFrameNamed> {
    fun label(items: Array<T>): String = StorageFrameCell(items[0]).value.name
}
private fun <T : StorageFrameNamed> storageFrameLabel(items: Array<T>): String = StorageFrameHolder<T>().label(items)
private open class StorageFrameRunner {
    open fun <T : StorageFrameNamed> run(items: Array<T>): String = StorageFrameHolder<T>().label(items)
}

class CallableStorageForwarderTests {
    @TestAttribute fun methodOnlyStorageForwardingDoesNotStrengthenTheOwner() {
        makeStorageFrameOwner<StorageFrameItem>()
        val owner = StorageFrameOwner<StorageFrameItem>()
        owner.use(StorageFrameItem("value"))
        check(owner.calls == 1)
    }

    @TestAttribute fun virtualBodyStorageKeepsItsErasedDispatchContract() {
        val values = arrayOf(StorageFrameItem("value"))
        check(storageFrameLabel(values) == "value")
        check(StorageFrameRunner().run(values) == "value")
    }
}
