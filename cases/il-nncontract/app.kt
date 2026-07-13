// #6: non-null PARAMETER PRECONDITIONS on the public surface. JVM Kotlin inserts
// Intrinsics.checkNotNullParameter at public-function entry; DotKt synthesizes the same fail-fast check
// (a plain NullPointerException, resolved to the BCL exception by bir2cir) for each NON-NULL REFERENCE
// value parameter of a PUBLIC/PROTECTED member — so a null crossing a boundary (a platform type, an
// unsound cast, reflection ignoring NRT) into a Kotlin non-null param throws AT ENTRY instead of
// propagating silently to a later, mis-sited NullReferenceException. A normal non-null call is
// unaffected and stays ilverify-clean.

@Suppress("UNCHECKED_CAST")
fun <T> forceNull(): T = null as T   // launder a null into a non-null reference slot (unchecked cast — the platform-type stand-in)

fun greet(s: String): Int = s.length            // public top-level fun: param precondition

class Box(val name: String) {                    // public ctor: param precondition
    fun tag(x: String): String = x + name        // public member fun: param precondition
}

fun main() {
    // normal non-null calls are unaffected
    println(greet("hi"))                          // 2
    println(Box("b").tag("t"))                    // tb

    // a null across the boundary -> fail-fast NullPointerException at each entry
    val nullStr: String = forceNull()
    try { greet(nullStr) } catch (e: NullPointerException) { println("npe-param") }
    try { Box(nullStr) } catch (e: NullPointerException) { println("npe-ctor") }
    try { Box("b").tag(nullStr) } catch (e: NullPointerException) { println("npe-member") }
}
