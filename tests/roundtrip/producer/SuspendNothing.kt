package roundtrip.suspendnothing

suspend fun fail(): Nothing = throw RuntimeException("unreachable")
suspend fun none(): Nothing? = null
