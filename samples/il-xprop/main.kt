// ...read and written from another file -> must resolve to StateKt.counter, not MainKt.counter.
fun bump() { counter = counter + 1 }
fun main() { bump(); bump(); counter = counter + 5; println(counter) }   // 7
