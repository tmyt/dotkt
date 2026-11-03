fun consumeNativeArray(value: System.Array) {}

fun invalidNativeArrayArguments() {
    consumeNativeArray("not an array")
    consumeNativeArray(listOf(1))
}
