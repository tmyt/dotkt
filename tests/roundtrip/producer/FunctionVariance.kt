package roundtrip.functionvariance

fun increment(): (Int) -> Int = { it + 1 }
fun sequenceLength(): (CharSequence?) -> Int? = { it?.length }
fun stringFactory(): () -> String = { "cross-module" }
fun consumeSequenceFactory(factory: () -> CharSequence): Int = factory().length
fun <T, R> keep(callback: (T) -> R): (T) -> R = callback
class FunctionHolder(val callback: (Int) -> Int)
fun invokeDefault(value: Int, callback: (Int) -> Int = increment()): Int = callback(value)

fun optional(present: Boolean): ((String?) -> String?)? = if (present) ({ it }) else null
fun nested(): (String?) -> ((Int) -> String?) = { text -> { count -> if (count > 0) text else null } }
fun extension(): String?.(Int) -> String? = { count -> if (count > 0) this else null }
class Context(val text: String?)
class CompanionFunctions
companion val CompanionFunctions.increment: (Int) -> Int get() = { it + 1 }
companion val CompanionFunctions.answer: Int get() = 42
fun contextual(): context(Context) (Int) -> String? = { count -> if (count > 0) contextOf<Context>().text else null }
class Box<T>(val value: T)
interface FunctionSink<T>
class IntFunctionSink : FunctionSink<(Int) -> Int>
class FunctionBound<F : (Int) -> Int>(val callback: F) {
    fun apply(value: Int): Int = callback(value)
}
fun <T> consumeBox(box: Box<T>): T = box.value
fun <T> consumeFactoryBox(factory: () -> Box<T>): T = consumeBox(factory())
fun <T> boxValue(box: Box<T>): T = box.value
fun <T> boxReader(): (Box<T>) -> T = ::boxValue
fun boxedStar(box: Box<*>): Any? = box.value
fun boxed(): Box<(Int) -> Int> = Box(increment())
inline fun inlineIncrement(value: Int, callback: (Int) -> Int = { it + 1 }): Int = callback(value)

fun selected(callback: () -> Float): Float = callback()
fun selected(callback: () -> Double): Double = callback()

interface ReturnSlots {
    fun selected(callback: () -> Float): Float
    fun selected(callback: () -> Double): Double
}

class ReturnImplementation : ReturnSlots {
    override fun selected(callback: () -> Float): Float = callback()
    override fun selected(callback: () -> Double): Double = callback()
}

class GenericReturnSlots<T> {
    fun selected(value: T, callback: (T) -> Float): Float = callback(value)
    fun selected(value: T, callback: (T) -> Double): Double = callback(value)
}

open class ConstructorCallbacks {
    val selected: Int
    constructor(callback: (Int) -> Unit) { callback(3); selected = 3 }
    constructor(callback: (String) -> Unit) { callback("four"); selected = 4 }
    constructor(callback: (Int) -> Unit, marker: Boolean): this(callback)
    constructor(callback: (String) -> Unit, marker: Boolean): this(callback)
}

interface CallbackSlots {
    fun select(callback: (Int) -> Unit): Int
    fun select(callback: (String) -> Unit): Int
}

class CallbackImplementation : CallbackSlots {
    override fun select(callback: (Int) -> Unit): Int { callback(5); return 5 }
    override fun select(callback: (String) -> Unit): Int { callback("six"); return 6 }
}

class DelegatedCallbacks(target: CallbackSlots) : CallbackSlots by target

interface GenericCallbackSlots<T> {
    fun select(callback: (T) -> Unit): Int
    fun select(callback: (String) -> Unit): Int
}

open class GenericCallbackBase<T>(private val value: T) : GenericCallbackSlots<T> {
    override fun select(callback: (T) -> Unit): Int { callback(value); return 7 }
    override fun select(callback: (String) -> Unit): Int { callback("eight"); return 8 }
}

class CallbackOuter<T>(private val value: T) {
    inner class Inner(private val callback: (T) -> Unit) {
        fun fire() { callback(value) }
    }
}

fun callbackInnerFactory(): Int {
    var calls = 0
    val outer: CallbackOuter<*> = CallbackOuter("value")
    val inner = outer.Inner { calls++ }
    inner.fire()
    return calls
}

open class CallbackParent {
    fun handle(callback: (Int) -> Unit): Int { callback(31); return 31 }
    fun handle(callback: (String) -> Unit): Int { callback("thirty-two"); return 32 }
}

interface InheritedCallbackSlot {
    fun handle(callback: (Int) -> Unit): Int
    fun handle(callback: (String) -> Unit): Int
}

class InheritedCallbackChild : CallbackParent(), InheritedCallbackSlot

open class GenericInheritedCallbackParent<T>(private val value: T) {
    fun handle(callback: (T) -> Unit): Int { callback(value); return 41 }
    fun handle(callback: (String) -> Unit): Int { callback("generic-inherited"); return 42 }
}

interface GenericInheritedCallbackSlot<T> {
    fun handle(callback: (T) -> Unit): Int
    fun handle(callback: (String) -> Unit): Int
}

class GenericInheritedCallbackChild : GenericInheritedCallbackParent<Int>(40), GenericInheritedCallbackSlot<Int>
