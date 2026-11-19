package pluginprobe.api

@Target(AnnotationTarget.FUNCTION, AnnotationTarget.TYPE, AnnotationTarget.PROPERTY_GETTER)
@Retention(AnnotationRetention.BINARY)
annotation class Marker

@Marker
fun <T> render(content: @Marker () -> T): Int = -1

fun render(content: Int): Int = content

class Tag
class Sample { @get:Marker val number: Int get() = 19 }
class CompositeBox<T>(var value: T)
@Marker
companion fun Tag.marked(content: @Marker () -> Int): Int = -1

class Scope<A> {
    class Nested<T> {
        @Marker
        fun <R> render(value: T, content: @Marker (T) -> R): Int = -1
    }
}
