// P2 (13): a generic factory `fun <T> state(i): State<T>` keeps State<T> constructed (gp:T), not the open type.
class State<T>(val value: T)
fun <T> state(i: T): State<T> = State(i)
fun main() { println(state(42).value); println(state("hi").value) }   // 42 / hi
