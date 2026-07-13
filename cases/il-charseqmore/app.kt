fun pick(c: Boolean, other: CharSequence): List<String> = (if (c) "a-b-c" else other).split("-")

fun main() {
    // (#149-3) a String BRANCH inside a polymorphic CharSequence-typed if/else
    for (s in pick(true, "z")) println("C:$s")
    // (#149-4) x!!.isNullOrEmpty() — nullable CharSequence? slot + a `!!` non-null value
    val nn: String? = "hi"
    println(if (nn!!.isNullOrEmpty()) "E:empty" else "E:nonempty")
    // (#149-2) StringBuilder (a non-String CharSequence) -> CharSequence.split
    val sb = StringBuilder("p\nq")
    for (s in sb.split("\n")) println("B:$s")
}
