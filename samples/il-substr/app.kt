// Kotlin String.substring(start, end) uses an EXCLUSIVE END index; .NET Substring(start, length) uses a LENGTH.
// The 2-arg form must convert end -> (end - start). The 1-arg form matches as-is.
fun main() {
    val s = "hello world"
    println(s.substring(1, 4))    // ell  (end exclusive; was "ell" + 'o' = "ello" when mismapped)
    println(s.substring(6))       // world
    println(s.substring(0, 5))    // hello
    println(s.substring(6, 11))   // world
}
