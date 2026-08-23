package roundtrip.ownership

fun interface ValueReader {
    fun read(): String
}

class AccessorOwner(private val value: String) {
    val reader: () -> String
        get() = { value }
}

interface DefaultInterfaceOwner {
    fun reader(value: String): () -> String = { value }
}

class DefaultInterfaceOwnerImpl : DefaultInterfaceOwner

class GenericMemberDefaultOwner<T>(val value: T) {
    fun render(block: () -> String = {
        fun read(): String = value.toString()
        read()
    }): String = block()
}

open class ProtectedNestedOwner {
    protected class HiddenNested(val value: Int)
    fun value(): Int = HiddenNested(19).value
}

interface OwnedRichEnumContract {
    fun contractMarker(): Int
}

enum class OwnedRichEnum : OwnedRichEnumContract {
    FIRST {
        override fun marker(): Int = 17
        override fun toString(): String = "custom"
    };

    override fun contractMarker(): Int = 18
    abstract fun marker(): Int
}

enum class OwnedRichLambdaEnum {
    FIRST {
        override fun reader(): () -> Int {
            val value = 61
            return { value }
        }
    };

    abstract fun reader(): () -> Int
}

enum class PlainNestedEnum {
    ONLY;

    class Helper(val value: Int)
}

// Kotlin permits an inner declaration to shadow an enclosing type-parameter name. The CLR flattens both generic
// slots onto the nested TypeDef, so their physical metadata names must be distinct even though their semantic names
// are both T.
class ShadowOwner<T : Comparable<T>>(private val outer: T) {
    inner class Entry<T>(private val inner: T) {
        fun joined(): String = outer.toString() + ":" + inner.toString()
    }
}

// Only B is referenced by the lambda. Its nested state-machine carrier must still re-declare the complete A,B owner
// prefix, or the captured field keeps `!1` while the carrier declares only one generic slot.
class SparseGenericSuspendOwner<A : Comparable<A>, B>(private val value: B) {
    fun make(): suspend () -> B = { value }
}

fun <A, B> sparseGenericSuspend(value: B): suspend () -> B = { value }

private fun <A, B> selectSecond(first: A, second: B): B = second

fun <A, B> sparseGenericLocalFunction(value: B): B {
    fun read(): B = selectSecond(Unit, value)
    return read()
}

fun <A> nestedGenericLocalFunction(value: A): String {
    fun <B> render(suffix: B): String = value.toString() + ":" + suffix.toString()
    return render(47)
}

fun defaultLambdaWithLocalFunction(block: () -> Int = {
    fun read(): Int = 48
    read()
}): Int = block()

fun defaultCapturingLambdaWithLocalFunction(seed: Int, block: () -> Int = {
    fun read(): Int = seed
    read()
}): Int = block()

fun <A, B> localFunctionWithLocalClass(value: B): String {
    fun render(item: B): String {
        class Holder(val held: B)
        return Holder(item).held.toString()
    }
    return render(value)
}

inline fun <T> invokeTwice(block: () -> T): Pair<T, T> = Pair(block(), block())

inline fun inlineOwnedReader(crossinline block: () -> String): ValueReader = ValueReader { block() }

fun localSuspendFunctionReference(value: Int): suspend () -> Int {
    suspend fun read(): Int = value
    return ::read
}

class Owner<T : Comparable<T>>(private val value: T) {
    class Nested(val value: Int) {
        fun doubled(): Int = value * 2
    }

    inner class Inner(private val extra: T) {
        fun joined(): String = value.toString() + ":" + extra.toString()
    }

    fun localClassValue(delta: Int): Int {
        class Local(private val add: Int) {
            fun read(): Int = value.toString().length + add
        }
        return Local(delta).read()
    }

    fun anonymousValue(): ValueReader = object : ValueReader {
        override fun read(): String = value.toString()
    }

    fun closureValue(): () -> String = { value.toString() }

    fun localFunctionValue(suffix: String): String {
        fun render(): String = value.toString() + suffix
        return render()
    }

    fun nestedLocalTypeFrames(): String {
        fun outer(): String {
            fun <U> inner(item: U): String = item.toString()
            return value.toString() + ":" + inner(7)
        }
        return outer()
    }

    fun localSuspendValue(): suspend () -> T {
        suspend fun read(): T = value
        return ::read
    }

    fun <U> genericLocalSuspendValue(item: U): suspend () -> U {
        suspend fun read(): U = item
        return ::read
    }

    fun <T> shadowedGenericLocalSuspend(item: T): suspend () -> T {
        suspend fun read(): T = item
        return ::read
    }

    fun localFunctionFromClosure(seed: Int): () -> Int {
        fun increment(): Int = seed + 1
        return { increment() }
    }

    fun localFunctionFromLocalClass(seed: Int): Int {
        fun increment(): Int = seed + 1
        class Caller {
            fun call(): Int = increment()
        }
        return Caller().call()
    }

    fun localFunctionFromGenericLocal(): String {
        fun render(): String = value.toString()
        class Caller<U>(private val ignored: U) {
            fun call(): String = render()
        }
        return Caller(0).call()
    }

    fun localFunctionInsideGenericLocal(): String {
        class Caller<U>(private val item: U) {
            fun call(): String {
                fun render(): String = value.toString() + ":" + item.toString()
                return render()
            }
        }
        return Caller(7).call()
    }

    fun localGenericOwnArgumentMatchesCapture(): String {
        class Echo<U>(private val item: U) {
            fun read(): String = item.toString()
        }
        return Echo(value).read()
    }

    fun <T> shadowedGenericClosure(value: T): () -> T = { value }
}

class GenericInnerLocalOwner<T>(private val outer: T) {
    inner class Entry<U>(private val inner: U) {
        fun render(): String {
            fun local(): String = outer.toString() + ":" + inner.toString()
            return local()
        }
    }
}

// #555: consumers may derive from this exported owner and construct its inherited inner class with their derived
// `this`. The physical hidden parameter remains this immediate owner's constructed type, not the consumer subclass.
open class ReferencedInnerBase<T>(private val outer: T) {
    inner class Entry {
        private val inner: String
        constructor(value: Int) { inner = "i$value" }
        constructor(value: String) { inner = "s$value" }
        fun render(): String = outer.toString() + ":" + inner
    }
    inner class GenericEntry<E>(private val value: E) {
        fun render(): String = outer.toString() + ":g" + value.toString()
    }
    inner class DefaultEntry(private val value: String = "default") {
        fun render(): String = outer.toString() + ":" + value
    }
}

class MultiLevelInnerOwner<A>(private val outer: A) {
    inner class Middle<B>(private val middle: B) {
        inner class Leaf<C>(private val leaf: C) {
            fun render(): String = outer.toString() + ":" + middle.toString() + ":" + leaf.toString()

            fun localRender(): String {
                fun read(): String = outer.toString() + ":" + middle.toString() + ":" + leaf.toString()
                return read()
            }
        }
    }
}

class GenericNestedClosureOwner<T : Comparable<T>> {
    fun factory(): (Int) -> (() -> Int) = { value -> { value } }
}

class InitLocalFunctionOwner {
    var value: Int = 0

    init {
        fun initialize(): Int = 62
        value = initialize()
    }

    constructor(seed: Int)
    constructor(seed: String)
}

inline fun inlineNestedClosure(crossinline transform: (String) -> String): () -> String {
	val make = { value: String -> { transform(value) } }
	return make("inline")
}

fun makeMultiLevelInner(): MultiLevelInnerOwner<Int>.Middle<String>.Leaf<Int> =
    MultiLevelInnerOwner(56).Middle("middle").Leaf(57)

fun makeNested(value: Int): Owner.Nested = Owner.Nested(value)

fun shadowedInnerTypeParameters(): String {
    val owner = ShadowOwner(7)
    return owner.Entry("seven").joined()
}

fun makeShadowedInner(): ShadowOwner<Int>.Entry<String> {
    val owner = ShadowOwner(8)
    return owner.Entry("eight")
}

fun topLevelLocalValue(value: Int): Int {
    class TopLevelLocal(private val held: Int) {
        fun read(): Int = held
    }
    return TopLevelLocal(value).read()
}

fun accessorClosureValue(): String = AccessorOwner("accessor").reader()

fun defaultInterfaceClosureValue(): String = DefaultInterfaceOwnerImpl().reader("default")()
