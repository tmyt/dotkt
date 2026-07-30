package suspendref

class SuspendRefService(private val base: Int) {
    suspend fun fetch(delta: Int): Int = base + delta
}
