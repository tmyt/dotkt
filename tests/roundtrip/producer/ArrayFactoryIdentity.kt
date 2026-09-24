package roundtrip.arrayfactoryidentity

fun intArrayOf(size: Int, fill: Int): IntArray = IntArray(size) { fill }
fun arrayOf(value: String): Array<String> = kotlin.arrayOf("user:$value")
fun arrayOfNulls(size: Int): Array<String> = Array(size) { "initialized" }

fun verifyLocalArrayFactoryIdentity() {
    val custom = intArrayOf(3, 9)
    check(custom.size == 3 && custom[2] == 9)
    check(arrayOf("local")[0] == "user:local")
    check(arrayOfNulls(2)[1] == "initialized")

    check(kotlin.intArrayOf().size == 0)
    val source = kotlin.intArrayOf(4, 5)
    val copied = kotlin.intArrayOf(*source)
    check(copied !== source && copied.size == 2 && copied[1] == 5)
    copied[0] = 99
    check(source[0] == 4)
    val mixed = kotlin.intArrayOf(3, *source, 6)
    check(mixed.size == 4 && mixed[0] == 3 && mixed[3] == 6)

    check(kotlin.arrayOf<String>().size == 0)
    val strings = kotlin.arrayOf("a", "b")
    val forwarded = kotlin.arrayOf(*strings)
    check(forwarded !== strings && forwarded[1] == "b")
    val nullable = kotlin.arrayOf<Int?>(1, null)
    check(nullable[0] == 1 && nullable[1] == null)
    val sized = kotlin.arrayOfNulls<String>(2)
    check(sized.size == 2 && sized[0] == null)
}
