import DlgNrt.Api

fun main() {
    // Func<string?> return: the lambda body returns null — compiles only when the delegate return surfaces as String?.
    println(Api.RunNullable { null })
    println(Api.RunNullable { "hello" })
    // Func<string> return: non-null result.
    println(Api.RunNonNull { "world" })
    // Action<string?> param: `s` is String?, so the null-coalescing is legal.
    Api.Consume { s -> println(s ?: "<n>") }
}
