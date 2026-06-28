package mylib
fun greeting(name: String): String = "Hello, " + name.uppercase() + "!"
fun squares(n: Int): List<Int> {
    val r = ArrayList<Int>()
    var i = 1
    while (i <= n) { r.add(i * i); i++ }
    return r
}
