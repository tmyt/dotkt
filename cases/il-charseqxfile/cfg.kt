// A user class + a top-level fun in a SIBLING .kt of the SAME assembly. Their String-typed members are the
// cross-file receivers that main.kt routes into a stdlib CharSequence extension (#149-1).
class Cfg {
    val body: String get() = "a\nb\nc"
}

fun banner(): String = "x-y-z"
