package roundtrip.publishedoverrides

// Descendants precede their ancestors to exercise declaration-order-independent slot accessibility.
class Leaf : Derived<String>() {
    override fun hook(): Int = 3
    override fun hook(value: Int): Int = value + 2
    override fun select(first: String, second: String): String = second + "!"
    override var readOnly: Int = 8
    override var mutable: Int = 9
}

open class Derived<T> : Base<T>() {
    override fun hook(): Int = 2
    override fun hook(value: Int): Int = value + 1
    override fun hook(value: String): Int = value.length + 1
    override fun select(first: T, second: T): T = second
    override var readOnly: Int = 5
    override var mutable: Int = 6
    override suspend fun suspended(): Int = 12
}

open class Base<T> {
    @PublishedApi internal open fun hook(): Int = 1
    @PublishedApi internal open fun hook(value: Int): Int = value
    internal open fun hook(value: String): Int = value.length
    @PublishedApi internal open fun select(first: T, second: T): T = first
    @PublishedApi internal open val readOnly: Int get() = 3
    @PublishedApi internal open var mutable: Int = 4
    @PublishedApi internal open suspend fun suspended(): Int = 11
}

abstract class AbstractBase {
    @PublishedApi internal abstract fun operation(): Int
}

class Concrete : AbstractBase() {
    override fun operation(): Int = 10
}

open class PublicDerived : Base<String>() {
    public override fun hook(): Int = 13
}

// Lambda-less inline is an ordinary-call control. The lambda-taking entries exercise actual DLL-body splicing.
inline fun ordinaryHook(value: Base<String>): Int = value.hook()
inline fun spliceHook(value: Base<String>, block: (Int) -> Int): Int = block(value.hook() + value.hook(20))
inline fun <T> spliceSelect(value: Base<T>, first: T, second: T, block: (T) -> T): T =
    block(value.select(first, second))
inline fun spliceProperties(value: Base<String>, block: (Int) -> Int): Int {
    value.mutable = 12
    return block(value.readOnly + value.mutable)
}
inline fun spliceAbstract(value: AbstractBase, block: (Int) -> Int): Int = block(value.operation())
suspend fun callSuspended(value: Base<String>): Int = value.suspended()
fun ordinaryInternalControl(value: Derived<String>): Int = value.hook("control")
fun setNewAccessor(value: Derived<String>) { value.readOnly = 17 }

fun anonymousOverride(): Base<String> = object : Base<String>() {
    override fun hook(): Int = 15
}
fun localOverride(): Base<String> {
    class Local : Base<String>() { override fun hook(): Int = 16 }
    return Local()
}
