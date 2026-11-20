package pluginprobe.api

@Target(AnnotationTarget.FUNCTION, AnnotationTarget.TYPE, AnnotationTarget.PROPERTY_GETTER)
@Retention(AnnotationRetention.BINARY)
annotation class Marker

@Marker
fun <T> render(content: @Marker () -> T): Int = -1

fun render(content: Int): Int = content

class Tag {
    @System.Runtime.CompilerServices.CompilerGeneratedAttribute
    internal object NestedCodes { const val version = 44 }
}
internal interface Versioned
internal sealed class CodeGroup : Versioned {
    companion object { const val version = 45 }
}
internal object InternalCodes : CodeGroup() { const val version = 43 }
class Sample { @get:Marker val number: Int get() = 19 }
class Writable { @get:Marker var number: Int = 19 }
@get:Marker val transformedDefault: Int = 19
var customRead: Int = 10
    get() = field + 1
object Codes {
    const val code = 7
    @get:Marker val String.code: Int get() = 19
}
class CompositeBox<T>(var value: T)
interface Relay<T> {
    fun echo(value: T): T
    operator fun get(index: Int): T
}
class TextRelay : Relay<String> {
    override fun echo(value: String) = value
    override fun get(index: Int) = "nullable receiver $index"
}
@get:Marker val <T> CompositeBox<T>.number: Int get() = 19
@Marker
companion fun Tag.marked(content: @Marker () -> Int): Int = -1

class Scope<A> {
    class Nested<T> {
        @Marker
        fun <R> render(value: T, content: @Marker (T) -> R): Int = -1
    }
}
