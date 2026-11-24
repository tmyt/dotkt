package roundtrip.setterframe

import NUnit.Framework.TestAttribute
import roundtrip.companionstatics.Tag
import roundtrip.companionstatics.lastPayload
import roundtrip.companionstatics.payload
import roundtrip.extpropref.RefBox
import roundtrip.extpropref.auditValue
import roundtrip.nullableselfbound.Cell

private class Owner<T> {
    private inner class Disposer {
        fun put(array: Array<T>, value: T) { array[0] = value }
    }

    private inner class Node {
        private val slot = Cell<Disposer?>(null)
        var disposer: Disposer?
            get() = slot.value
            set(value) { slot.value = value }

        fun record(d: Disposer) {
            val box = RefBox<Disposer?>(null)
            box.auditValue = d
            check(box.value === d && box.auditValue === d)
            box.auditValue = null
            check(box.value == null)
        }
    }

    fun store(array: Array<T>, value: T) {
        val node = Node()
        val disposer = Disposer()
        node.record(disposer)
        node.disposer = disposer
        check(node.disposer === disposer)
        node.disposer!!.put(array, value)
        node.disposer = null
        check(node.disposer == null)
        lastPayload = value
        check(Tag.payload === value)
        lastPayload = null
        Tag.payload = value
        check(lastPayload === value)
        Tag.payload = null
    }
}

class ImportedSetterFrameTests {
    @TestAttribute
    fun setterUsesItsDeclarationFrameAcrossAssemblies() {
        val array = arrayOf("before")
        Owner<String>().store(array, "after")
        check(array[0] == "after")
    }
}
