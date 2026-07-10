// #73 M2 regression gate: `x in a..b` on a USER type with `operator fun rangeTo` + `operator fun contains`
// must dispatch the USER-defined contains() — the old bare-name lowering MISCOMPILED it to primitive
// `x >= a && x <= b` comparisons (nonsensical on a user object). The "user contains" print proves the real
// method runs; the primitive `x in lo..hi` inside it exercises the bir2cir range-membership fast path too.
class Version(val major: Int, val minor: Int) {
    operator fun rangeTo(other: Version) = VersionRange(this, other)
    fun code(): Int = major * 100 + minor
}

class VersionRange(val start: Version, val end: Version) {
    operator fun contains(v: Version): Boolean {
        println("user contains")
        return v.code() in start.code()..end.code()
    }
}

fun main() {
    val lo = Version(1, 0)
    val hi = Version(2, 5)
    println(Version(1, 5) in lo..hi)  // user contains \n true
    println(Version(3, 0) in lo..hi)  // user contains \n false
}
