import NUnit.Framework.TestAttribute

private class MultiGridChild : MultiIndexers.Grid()
private class ProtectedMultiGrid<T>(initial: T) : MultiIndexers.ProtectedGrid<String, T>(initial) {
    fun read(): T = this["row", 7, "tag"]
    fun write(value: T) { this["row", 7, "tag"] = value }
    fun captured(): () -> T = { this["row", 7, "tag"] }
}

private fun <T> multiIndexMark(log: MutableList<String>, label: String, value: T): T {
    log.add(label)
    return value
}

private fun multiIndexThrow(log: MutableList<String>): Int {
    log.add("X")
    throw IllegalStateException("index")
}

class MultiIndexerTests {
    @TestAttribute
    fun allIndicesAndAssignedValueKeepSourceOrder() {
        val grid = MultiGridChild()
        val log = mutableListOf<String>()
        multiIndexMark(log, "R", grid)[multiIndexMark(log, "A", 2), multiIndexMark(log, "B", 3)] =
            multiIndexMark(log, "V", 41)
        check(log.joinToString("") == "RABV")
        check(grid.Stored == 18)
        log.clear()
        val result = multiIndexMark(log, "R", grid)[multiIndexMark(log, "A", 2), multiIndexMark(log, "B", 3)]
        check(result == 41)
        check(log.joinToString("") == "RAB")
        log.clear()
        multiIndexMark(log, "R", grid)[multiIndexMark(log, "A", 2), multiIndexMark(log, "B", 3)] +=
            multiIndexMark(log, "V", 5)
        check(log.joinToString("") == "RABV")
        check(grid[2, 3] == 46)
        grid[2, "abc"] = 151
        check(grid.Stored == 28)
        check(grid[2, "abc"] == 151)
        grid[1, 2, 3] = 300
        check(grid.Stored == 177)
        check(grid[1, 2, 3] == 300)
        log.clear()
        var failed = false
        try {
            multiIndexMark(log, "R", grid)[multiIndexThrow(log), multiIndexMark(log, "B", 3)] =
                multiIndexMark(log, "V", 41)
        } catch (e: IllegalStateException) {
            failed = true
            check(e.message == "index")
        }
        check(failed)
        check(log.joinToString("") == "RX")
        check(grid.Stored == 177)
    }

    @TestAttribute
    fun protectedGenericMultiIndexerKeepsEverySlot() {
        val strings = ProtectedMultiGrid("initial")
        val captured = strings.captured()
        check(strings.read() == "initial")
        strings.write("changed")
        check(captured() == "changed")
        check(strings.LastKey == "row")
        check(strings.LastColumn == 7)
        check(strings.LastTag == "tag")
        val integers = ProtectedMultiGrid(19)
        check(integers.read() == 19)
        integers.write(37)
        check(integers.captured()() == 37)
        val nullable = ProtectedMultiGrid<String?>(null)
        check(nullable.read() == null)
        nullable.write("present")
        check(nullable.captured()() == "present")
        nullable.write(null)
        check(nullable.read() == null)
    }
}
