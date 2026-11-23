package roundtriptests.builderconstruction

private class Text(val value: CharSequence?)

internal fun checkBuilderConstructorSnapshot() {
    val builder = StringBuilder("abc")
    var evaluations = 0
    fun read(value: StringBuilder?): StringBuilder? { evaluations++; return value }
    val direct = Text(builder)
    val nullable = Text(read(builder))
    val missing = Text(read(null))
    builder.append('d')
    check(evaluations == 2 && direct.value?.length == 3 && nullable.value?.length == 3 && missing.value == null)
}
