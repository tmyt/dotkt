// bir2cir SuspendColdLowering P3 — the GENERIC SM spike. A generic `suspend fun <T>` lowers to a
// generic state machine `<file>_f$sm<T>` with T-typed fields + a generic cold entry; invokeSuspend
// returns object (boxing a value T), and an awaited T is read back via `unbox.any !T`.

suspend fun <T> idw(x: T): T = x                       // GEN1: no suspension -> a generic direct cold entry

suspend fun <T> passthru(x: T): T {                    // GEN2: generic + a suspend call (await temp typed T)
    val y = idw(x)
    return y
}

suspend fun main() {
    println(idw(7))              // value T = Int
    println(idw("yo"))           // reference T = String
    println(passthru(8))         // value T through a suspension
    println(passthru("hi"))      // reference T through a suspension
}
