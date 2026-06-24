// P2 (10): `object` singleton as shared state — Counter.INSTANCE holds it; member access routes as instance access.
object Counter { var n = 0; fun inc() { n = n + 1 } }
fun main() { Counter.inc(); Counter.inc(); Counter.inc(); println(Counter.n) }   // 3
