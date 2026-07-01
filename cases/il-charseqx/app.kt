// Cross-assembly CharSequence: a STDLIB CharSequence-extension (kotlin.text.hasSurrogatePairAt, in the rt
// DotKt.Stdlib.dll, NOT a kotc STRING_OPS lowering) called with an APP value. Both a user `class S : CharSequence`
// and a String (wrapped by bir2cir's foundation-A adapter) must reach the extension's `<>dotkt_CharSequence` param.
// This only works when the synthetic interface is CANONICAL (emitted once in the rt dll, referenced here) — with a
// per-assembly copy the app value implements a DISTINCT CLR type and the call throws EntryPointNotFound.
class S(val s: String) : CharSequence {
  override val length: Int get() = s.length
  override fun get(index: Int): Char = s[index]
  override fun subSequence(startIndex: Int, endIndex: Int): CharSequence = S(s.substring(startIndex, endIndex))
}
fun main() {
  println(S("hello").hasSurrogatePairAt(0))   // False — user CharSequence -> stdlib ext
  println("hi".hasSurrogatePairAt(0))          // False — String -> adapter -> stdlib ext
}
