package mylib
fun greeting(name: String): String = "Hello, " + name.uppercase() + "!"
fun sumTo(n: Int): Int { var s = 0; for (i in 1..n) s += i; return s }
