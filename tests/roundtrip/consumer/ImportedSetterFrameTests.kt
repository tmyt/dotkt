package roundtrip.setterframe

import NUnit.Framework.TestAttribute
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
    }

    fun store(array: Array<T>, value: T) {
        val node = Node()
        val disposer = Disposer()
        node.disposer = disposer
        check(node.disposer === disposer)
        node.disposer!!.put(array, value)
        node.disposer = null
        check(node.disposer == null)
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
