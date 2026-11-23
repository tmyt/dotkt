package roundtriptests.builderconstruction

private class Text(val value: CharSequence?)
class RequiredText(val value: CharSequence)

internal fun checkBuilderConstructorSnapshot() {
    val builder = StringBuilder("abc")
    var evaluations = 0
    fun read(value: StringBuilder?): StringBuilder? { evaluations++; return value }
    val direct = Text(builder)
    val nullable = Text(read(builder))
    val missing = Text(read(null))
    fun fromAny(x: Any?) = Text(x as? StringBuilder)
    val matched = fromAny(builder)
    val missed = fromAny(42)
    builder.append('d')
    check(evaluations == 2 && direct.value?.length == 3 && nullable.value?.length == 3 && missing.value == null)
    check(matched.value?.length == 3 && missed.value == null)
    val platform = System.Threading.ThreadLocal<StringBuilder>().Value
    check(Text(platform).value == null)
    try {
        RequiredText(platform)
        error("A null platform builder reached a non-null constructor slot")
    } catch (_: NullPointerException) {}
}
