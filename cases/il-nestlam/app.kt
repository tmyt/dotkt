fun main() {
    val fs = (1..3).map { i -> { i } }
    println(fs.map { it() })
}
