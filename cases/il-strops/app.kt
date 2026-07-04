// IL parity: kotlin.text String ops run the PURE-KOTLIN stdlib bodies (NO kotc STRING_OPS -> System.String lowering).
// Covers the ops migrated out of kotc in bundle-8: trim(vararg)/trimStart(vararg)/trimEnd(vararg), padStart/padEnd
// (both defaulted and explicit padChar), replace(String,String) and replace(Char,Char).
fun main() {
    println("xxhelloxx".trim('x'))        // hello
    println("**hi".trimStart('*'))        // hi
    println("hi!!".trimEnd('!'))          // hi
    println("5".padStart(3))              //   5   (default pad space)
    println("5".padStart(3, '0'))         // 005
    println("5".padEnd(3, '0'))           // 500
    println(">" + "5".padEnd(3) + "<")    // >5  <  (default pad space)
    println("hello".replace("l", "L"))    // heLLo
    println("aaa".replace("a", "bb"))     // bbbbbb
    println("abcabc".replace("bc", "X"))  // aXaX
    println("hello".replace('l', 'L'))    // heLLo
}
