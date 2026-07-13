// #6: non-null CONTRACTS on the public surface. JVM Kotlin inserts Intrinsics.checkNotNullParameter at
// public-function entry; DotKt synthesizes the same fail-fast PARAMETER PRECONDITION (a plain
// NullPointerException, resolved to the BCL exception by bir2cir) for each NON-NULL REFERENCE value
// parameter of a PUBLIC/PROTECTED member, AND a DotKt-specific non-null RETURN POSTCONDITION guarding a
// null leaking OUT. So a null crossing a boundary (a platform type, an unsound cast, reflection ignoring
// NRT) throws AT THE CONTRACT instead of propagating to a later, mis-sited NullReferenceException. Normal
// non-null calls are unaffected and stay ilverify-clean.

@Suppress("UNCHECKED_CAST")
fun <T> forceNull(): T = null as T   // launder a null into a non-null reference slot (unchecked cast — the platform-type stand-in)

fun greet(s: String): Int = s.length            // public top-level fun: param precondition

class Box(val name: String) {                    // public ctor: param precondition
    fun tag(x: String): String = x + name        // public member fun: param precondition
    val leakyProp: String get() = forceNull()    // public getter: return postcondition
    fun leakM(): String = forceNull()            // public member fun: return postcondition
}

fun leak(): String = forceNull()                 // public top-level fun: return postcondition

fun leakInTry(): String {                        // return POSTCONDITION wrap evaluated INSIDE a try region
    try { return forceNull() } finally { println("fin") }   // NPE thrown in-try -> finally runs, then propagates
}

fun main() {
    // normal non-null calls are unaffected
    println(greet("hi"))                          // 2
    println(Box("b").tag("t"))                    // tb

    // PRECONDITIONS: a null across the boundary -> fail-fast NullPointerException at each entry
    val nullStr: String = forceNull()
    try { greet(nullStr) } catch (e: NullPointerException) { println("npe-param") }
    try { Box(nullStr) } catch (e: NullPointerException) { println("npe-ctor") }
    try { Box("b").tag(nullStr) } catch (e: NullPointerException) { println("npe-member") }

    // POSTCONDITIONS: a null leaking OUT of a non-null return -> NullPointerException at the return
    try { leak() } catch (e: NullPointerException) { println("npe-ret") }
    try { Box("b").leakM() } catch (e: NullPointerException) { println("npe-retm") }
    try { Box("b").leakyProp } catch (e: NullPointerException) { println("npe-getter") }
    try { leakInTry() } catch (e: NullPointerException) { println("npe-trret") }   // finally runs first ("fin"), then the postcondition NPE propagates
}
