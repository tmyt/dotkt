package roundtrip.castcontract

class CastExceptionApi {
    fun failValue(value: Any): Int = value as Int
    fun failClassifier(value: Any): Number = value as Number
    fun failAsKotlin(message: String): Nothing = throw ClassCastException(message)
    fun construct(message: String): ClassCastException = ClassCastException(message)
}
