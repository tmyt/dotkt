import NUnit.Framework.TestAttribute

private fun liftedLocalInner(): Int {
    class Local(val value: Int) {
        inner class Capture { fun read(): Int = value }
    }
    return Local(5).Capture().read()
}

private fun liftedAnonymousInner(): Int {
    val outer = object {
        val value = 7
        inner class Capture { fun read(): Int = value }
        fun read(): Int = Capture().read()
    }
    return outer.read()
}

private fun <T> liftedGenericLocal(seed: T): T {
    class Local<U>(val first: U) {
        inner class Capture<V>(val last: V) {
            fun read(): Triple<T, U, V> = Triple(seed, first, last)
        }
    }
    val result = Local(3).Capture(true).read()
    check(result.second == 3 && result.third)
    return result.first
}

private fun <T> liftedGenericAnonymous(seed: T): T {
    val outer = object {
        inner class Capture { fun read(): T = seed }
        fun read(): T = Capture().read()
    }
    return outer.read()
}

private fun liftedInnerDescendants(): Pair<Int, String> {
    class Local(val value: Int) {
        inner class Middle<U>(val middle: U) {
            inner class Leaf { fun read(): Pair<Int, U> = Pair(value, middle) }
        }
    }
    return Local(9).Middle("middle").Leaf().read()
}

private fun <T> liftedInnerDefault(seed: T): T {
    class Local {
        inner class Capture(val value: T = seed)
    }
    return Local().Capture().value
}

private fun liftedInnerMutableCapture(): Int {
    var value = 1
    class Local {
        inner class Capture {
            fun read(): Int = value
            fun write(next: Int) { value = next }
        }
    }
    val capture = Local().Capture()
    value = 2
    check(capture.read() == 2)
    capture.write(3)
    return value
}

private fun liftedInnerLocalBase(seed: Int): Int {
    var value = seed
    open class Base {
        fun read() = value
        fun write(next: Int) { value = next }
    }
    class Local { inner class Capture : Base() }
    val capture = Local().Capture()
    value++
    check(capture.read() == seed + 1)
    capture.write(seed + 2)
    return value
}

private fun liftedInnerOwnerDefault(seed: Int, prior: Any? = null): Int {
    class Local { inner class Capture(val value: Int = seed) }
    return if (prior == null) liftedInnerOwnerDefault(seed + 1, Local())
    else (prior as Local).Capture().value
}

private fun liftedInnerDefaultReceiver(seed: Int): Int {
    class Local { inner class Capture(val value: Int = seed) }
    val owner = Local()
    var reads = 0
    val next = { reads++; owner }
    val make = { next().Capture().value }
    val result = make()
    check(reads == 1)
    return result
}

private class LiftedInnerGenericOwner<A>(val value: A) {
    fun read(): Triple<A, String, Int> {
        class Local<U>(val middle: U) {
            inner class Capture<V>(val last: V) { fun read() = Triple(value, middle, last) }
        }
        return Local("middle").Capture(5).read()
    }
}

private fun <T> liftedInnerMethodBound(seed: T): T {
    class Local { inner class Capture { fun <U : T> read(value: U): U = value } }
    return Local().Capture().read(seed)
}

private fun <T> liftedInnerMarker(): Boolean = true

private fun <T> liftedInnerAccessorType(): Boolean {
    class Local { inner class Capture { val value: Boolean get() = liftedInnerMarker<T>() } }
    return Local().Capture().value
}

class LiftedInnerDeclarationTests {
    @TestAttribute
    fun liftedOwnersKeepInnerDeclarationsAndCompleteEnclosingFrames() {
        check(liftedLocalInner() == 5 && liftedAnonymousInner() == 7)
        check(liftedGenericLocal("local") == "local")
        check(liftedGenericLocal(42) == 42 && liftedGenericLocal<Int?>(null) == null)
        check(liftedGenericAnonymous("anonymous") == "anonymous")
        check(liftedGenericAnonymous(17) == 17 && liftedGenericAnonymous<Int?>(null) == null)
        check(liftedInnerDescendants() == Pair(9, "middle"))
        check(liftedInnerDefault("default") == "default")
        check(liftedInnerMutableCapture() == 3)
        check(liftedInnerLocalBase(10) == 12)
        check(liftedInnerOwnerDefault(41) == 41)
        check(liftedInnerDefaultReceiver(27) == 27)
        check(LiftedInnerGenericOwner(true).read() == Triple(true, "middle", 5))
        check(liftedInnerMethodBound("bound") == "bound" && liftedInnerMethodBound<Int?>(null) == null)
        check(liftedInnerAccessorType<String>())
    }
}
