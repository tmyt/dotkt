// String.format binds to System.String.Format with .NET composite format strings ({0:F2}, not %.2f); plus mapIndexed.
fun main() {
    println("{0} items".format(5))              // 5 items
    println("{0} = {1}".format("x", 42))         // x = 42
    println("{0:F2}".format(3.14159))            // 3.14
    println("{0:D5}".format(7))                  // 00007
    println("{0:x}".format(255))                 // ff
    println("100% ok: {0}".format("yes"))        // 100% ok: yes

    val xs = listOf("a", "b", "c")
    println(xs.mapIndexed { i, v -> "$i:$v" }.joinToString(","))             // 0:a,1:b,2:c
    println(listOf(10, 20, 30).mapIndexed { i, v -> i * v }.joinToString(",")) // 0,20,60
}
