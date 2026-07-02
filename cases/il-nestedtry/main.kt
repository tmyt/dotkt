fun f(): Int {
    try {
        try {
            return 1
        } finally {
            println("inner fin")
        }
    } finally {
        println("outer fin")
    }
}
fun main() {
    println(f())
}
