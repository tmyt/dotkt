// More stdlib: String.format (printf -> .NET composite format, literal translation) and mapIndexed.
fun main() {
    println("%d items".format(5))               // 5 items
    println("%s = %d".format("x", 42))          // x = 42
    println("%.2f".format(3.14159))             // 3.14
    println("%05d".format(7))                   // 00007
    println("%x".format(255))                   // ff
    println("100%% ok: %s".format("yes"))       // 100% ok: yes

    val xs = listOf("a", "b", "c")
    println(xs.mapIndexed { i, v -> "$i:$v" }.joinToString(","))             // 0:a,1:b,2:c
    println(listOf(10, 20, 30).mapIndexed { i, v -> i * v }.joinToString(",")) // 0,20,60
}
