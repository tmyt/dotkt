// `try`/`catch` in value position: expression-body, val initializer, and inside a lambda.
fun parse(s: String): Int = try { s.toInt() } catch (e: Exception) { -1 }

fun main() {
    println(parse("42"))                                   // 42
    println(parse("xx"))                                   // -1
    val x = try { 10 / 2 } catch (e: Exception) { 0 }
    println(x)                                             // 5
    val y = try { 10 / 0 } catch (e: ArithmeticException) { -7 }
    println(y)                                             // -7
    val z = listOf("1", "bad", "3").map { try { it.toInt() } catch (e: Exception) { 0 } }
    println(z.sum())                                       // 4
}
