package roundtrip.suspendnothing

suspend fun fail(): Nothing = throw RuntimeException("unreachable")
