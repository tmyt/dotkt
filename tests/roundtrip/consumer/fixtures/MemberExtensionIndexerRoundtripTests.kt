import NUnit.Framework.TestAttribute
import roundtrip.memberextensionindexers.Cell
import roundtrip.memberextensionindexers.GenericOps
import roundtrip.memberextensionindexers.Grid
import roundtrip.memberextensionindexers.Ops

private class DerivedExtensionOps : Ops(7) {
    override fun Grid.get(index: Int): Int = value + 100 + index
    override fun Grid.set(index: Int, newValue: Int) { value = newValue - 100 - index }
}

private class InheritedGenericExtensionOps : GenericOps<String>("key")

class MemberExtensionIndexerRoundtripTests {
    @TestAttribute
    fun importedExtensionIndexersKeepBothReceiversAndOrder() {
        var trace = ""
        val grid = Grid(11)
        fun dispatch(): Ops { trace += "D"; return Ops(7) }
        fun receiver(): Grid { trace += "R"; return grid }
        fun row(): Int { trace += "I"; return 3 }
        fun column(): Int { trace += "J"; return 5 }
        fun assigned(): Int { trace += "V"; return 41 }
        with(dispatch()) { check(receiver()[row()] == 21) }
        check(trace == "DRI")
        trace = ""
        with(dispatch()) { receiver()[row()] = assigned() }
        check(trace == "DRIV")
        check(grid.value == 31)
        trace = ""
        with(dispatch()) { receiver()[row(), column()] = assigned() }
        check(trace == "DRIJV")
        check(grid.value == 26)
        trace = ""
        with(dispatch()) { check(receiver()[row(), column()] == 41) }
        check(trace == "DRIJ")
    }

    @TestAttribute
    fun importedExtensionIndexersKeepOwnerAndMethodGenericFrames() {
        val number = Cell(23)
        with(InheritedGenericExtensionOps()) {
            check(number["key"] == 23)
            number["key"] = 29
            check(number["key"] == 29)
        }
        val text = Cell("before")
        with(GenericOps(7)) {
            check(text[7] == "before")
            text[7] = "after"
            check(text[7] == "after")
        }
    }

    @TestAttribute
    fun importedExtensionIndexersKeepVirtualDispatch() {
        val ops: Ops = DerivedExtensionOps()
        val grid = Grid(11)
        with(ops) {
            check(grid[3] == 114)
            grid[3] = 127
            check(grid.value == 24)
            check(grid[3] == 127)
        }
    }
}
