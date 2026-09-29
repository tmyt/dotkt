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
