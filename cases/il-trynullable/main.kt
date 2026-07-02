fun f(): Int? {
    try {
        return 1
    } finally {
        println("fin")
    }
}
fun main() { println(f()) }
