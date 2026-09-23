import NUnit.Framework.TestAttribute
import OmittedIndexers.Grid
import OmittedIndexers.ProtectedGrid
import OmittedIndexers.VarargGrid

private class OptionalIndexerView<A, B>(initial: B) : ProtectedGrid<A, B>(initial) {
    fun read(key: A): B = this[key]
    fun write(key: A, value: B) { this[key] = value }
}

class OmittedIndexerTests {
    @TestAttribute
    fun optionalIndicesKeepTheirPositionsAndSelectedDefaults() {
        val grid = Grid()
        var trace = ""
        fun receiver(): Grid { trace += "R"; return grid }
        fun index(): Int { trace += "I"; return 2 }
        fun value(): Int { trace += "V"; return 61 }
        receiver()[index()] = value()
        check(trace == "RIV")
        check(grid.Stored == 36)
        trace = ""
        check(receiver()[index()] == 61)
        check(trace == "RI")
        grid["abc"] = 83
        check(grid.Stored == 44)
        check(grid["abc"] == 83)
        check(grid["abc", 4] == 78)
    }

    @TestAttribute
    fun protectedGenericIndexersReadDefaultsFromTheirNamedAccessor() {
        val number = OptionalIndexerView<String, Int>(17)
        number.write("key", 29)
        check(number.LastKey == "key")
        check(number.LastColumn == 7)
        check(number.read("read") == 29)
        check(number.LastKey == "read")
        check(number.LastColumn == 7)
        val text = OptionalIndexerView<Int, String>("before")
        text.write(3, "after")
        check(text.read(5) == "after")
        check(text.LastKey == 5)
        check(text.LastColumn == 7)
    }

    @TestAttribute
    fun indexerVarargsSupplyEmptyAndNonemptyArrays() {
        val grid = VarargGrid()
        check(grid.get() == 17)
        check(grid.LastCount == 0)
        grid.set(value = 43)
        check(grid.LastCount == 0)
        check(grid.get() == 43)
        var trace = ""
        fun receiver(): VarargGrid { trace += "R"; return grid }
        fun first(): Int { trace += "I"; return 2 }
        fun second(): Int { trace += "J"; return 3 }
        fun value(): Int { trace += "V"; return 71 }
        receiver()[first(), second()] = value()
        check(trace == "RIJV")
        check(grid.LastCount == 2)
        trace = ""
        check(receiver()[first(), second()] == 71)
        check(trace == "RIJ")
        check(grid.get() == 69)
    }
}
