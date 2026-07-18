// #104: Sequence-returning Regex members (findAll / splitToSequence) + the options getter, all of which
// previously shipped as TODO() runtime stubs that threw NotImplementedError.
fun main() {
    val nums = Regex("\\d+")
    // findAll: every non-overlapping match, left-to-right (Kotlin contract), over ordinary Sequence machinery.
    println(nums.findAll("a1b22c333").map { it.value }.joinToString(","))   // 1,22,333
    println(nums.findAll("no digits here").count())                        // 0
    // findAll honors startIndex.
    println(nums.findAll("1a2a3", 2).map { it.value }.joinToString(","))    // 2,3

    val ws = Regex("\\s+")
    // splitToSequence: identical elements to split(), in order.
    val seq = ws.splitToSequence("a b  c").toList()
    println(seq.joinToString("|"))                                          // a|b|c
    println(seq == ws.split("a b  c"))                                      // true
    // splitToSequence honors limit.
    println(ws.splitToSequence("a b c d", 2).toList().joinToString("|"))    // a|b c d

    // options: a default Regex has no options (decodes to an empty set, no longer throws).
    println(Regex("x").options.isEmpty())                                  // true
}
