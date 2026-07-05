// Regression: Regex.replaceFirst / replace(String,String) marshaling (final-review N1).
// replaceFirst mis-bound the 3-arg System...Regex.Replace(string,string,int) — it returned the input
// unchanged and hard-crashed (AccessViolationException) on a CharSequence-typed input. The fix
// materializes the CharSequence to a real String at the call site so the intrinsic binds the exact
// 3-arg String overload. toString() (the method binding that returns the pattern string) is exercised
// too, since Regex.pattern reads through it.
fun main() {
    val a = "a".toRegex()
    println(a.replaceFirst("banana", "X"))      // bXnana  (was: banana, unchanged)
    println(a.replace("banana", "X"))           // bXnXnX
    val cs: CharSequence = "banana"
    println(a.replaceFirst(cs, "X"))            // bXnana  (was: AccessViolationException)
    println("[0-9]+".toRegex().replaceFirst("a12b34", "#"))  // a#b34
    println(Regex("a(\\d+)b").toString())       // a(\d+)b  (pattern-string source; method binding)
    // final-review N2: `re.pattern` (a rule-3 property accessor `get() = toString()`) MUST hoist into
    // the ClrH helper — AliasHelperHoist previously blanket-skipped get_/set_ so get_pattern was never
    // emitted -> ilemit crash. The getter reads NO backing field, so it now hoists.
    println(Regex("c(\\w+)d").pattern)          // c(\w+)d  (rule-3 accessor hoist)
}
