// A bounded generic star projection has no direct invariant CLR representation. bir2cir emits a non-generic
// existential interface for StarKey<*>, while [KotlinType] must restore the original projection when this DLL is
// consumed as Kotlin. Without the carrier, StarOwner.key and isConcreteStarKey's parameter re-import as Any?.
package starprojection

interface StarElement
interface StarKey<E : StarElement>

class ConcreteStarElement : StarElement
object ConcreteStarKey : StarKey<ConcreteStarElement>

interface StarOwner {
    val key: StarKey<*>
}

private class StarOwnerImpl : StarOwner {
    override val key: StarKey<*> get() = ConcreteStarKey
}

fun starOwner(): StarOwner = StarOwnerImpl()
fun isConcreteStarKey(key: StarKey<*>): Boolean = key === ConcreteStarKey

// The producer deliberately never star-projects this declaration. A downstream module is nevertheless free to
// consume the exported generic classifier as FirstUseBox<*>, so its existential CLR ABI cannot depend on whether this
// source module happened to use that projection itself.
interface FirstUseBox<T> {
    fun get(): T
}

private class FirstUseStringBox : FirstUseBox<String> {
    override fun get(): String = "first-use"
}

fun firstUseBox(): FirstUseBox<String> = FirstUseStringBox()

interface MixedBox<A, B> {
    fun first(): A
    fun second(): B
    fun choose(value: A): String
    fun choose(value: String): String
}

private class MixedValueBox : MixedBox<Int, String> {
    override fun first(): Int = 23
    override fun second(): String = "mixed"
    override fun choose(value: Int): String = "int:$value"
    override fun choose(value: String): String = "string:$value"
}

fun mixedBox(): MixedBox<*, String> = MixedValueBox()

interface CollisionHost<T> {
    // Keep source classifiers adjacent to every word in the compiler's preferred physical suffix. Kotlin source
    // cannot spell '$' even in an escaped identifier, so the allocator's exact reserved spelling is unspeakable;
    // association must nevertheless come only from metadata, never a fuzzy name match.
    class dotkt {
        class star
    }

    fun value(): T
}

private class CollisionHostImpl : CollisionHost<String> {
    override fun value(): String = "collision-safe"
}

fun collisionHost(): CollisionHost<*> = CollisionHostImpl()

interface ReferencedStarBase<T> {
    fun inherited(): T
}
