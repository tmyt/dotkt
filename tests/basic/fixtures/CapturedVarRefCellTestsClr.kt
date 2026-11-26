// Shares a file facade with CapturedVarRefCellTests.kt, but declares its own captured Int.
internal fun capturedVarRefCellInSiblingFile(): Int {
    var n = 1
    repeat(2) { n++ }
    return n
}
