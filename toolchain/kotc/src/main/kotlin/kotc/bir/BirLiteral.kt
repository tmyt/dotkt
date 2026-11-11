package kotc.bir

sealed class BirLiteral : BirValue {
    data object NullLiteral : BirLiteral() {
        override fun writeJson(output: StringBuilder) { output.append("null") }
    }
    data class BooleanLiteral(val value: Boolean) : BirLiteral() {
        override fun writeJson(output: StringBuilder) { output.append(value) }
    }
    data class IntegerLiteral(val value: Long) : BirLiteral() {
        override fun writeJson(output: StringBuilder) { output.append(value) }
    }
    data class TextLiteral(val value: String) : BirLiteral() {
        override fun writeJson(output: StringBuilder) { LiteralText.writeJson(value, output) }
    }

    abstract override fun writeJson(output: StringBuilder)
    fun toJson(): String = buildString { writeJson(this) }

    companion object {
        fun fromValue(value: Any?): BirLiteral = when (value) {
            null -> NullLiteral
            is String -> TextLiteral(value)
            is Boolean -> BooleanLiteral(value)
            is Char -> IntegerLiteral(value.code.toLong())
            is Byte -> IntegerLiteral(value.toLong())
            is Short -> IntegerLiteral(value.toLong())
            is Int -> IntegerLiteral(value.toLong())
            is Long -> IntegerLiteral(value)
            is Float -> Float32Literal(value.toRawBits())
            is Double -> Float64Literal(value.toRawBits())
            else -> error("Unexpected Kotlin IR scalar value")
        }
    }
}
