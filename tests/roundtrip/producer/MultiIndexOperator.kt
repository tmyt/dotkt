package roundtrip.multiindex

open class Slot<T>(private var value: T) {
    var lastRow: Int = -1
    var lastColumn: String = ""
    operator fun get(row: Int, column: String): T {
        lastRow = row
        lastColumn = column
        return value
    }
    operator fun set(row: Int, column: String, replacement: T) {
        lastRow = row
        lastColumn = column
        value = replacement
    }
}
