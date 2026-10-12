package roundtrip.multimodulesuspend

import roundtrip.inheritedsuspend.Gate

suspend fun <T> awaitFromSecondModule(gate: Gate<T>): T = gate.await()
