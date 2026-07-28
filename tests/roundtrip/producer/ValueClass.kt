package roundtrip.valueclass

@JvmInline
value class Token internal constructor(internal val raw: Int) {
    fun doubled(): Int = raw * 2
}

fun tokenOf(raw: Int): Token = Token(raw)
fun tokenValue(token: Token): Int = token.doubled()
