package pluginprobe.api

@Target(AnnotationTarget.FUNCTION, AnnotationTarget.TYPE)
@Retention(AnnotationRetention.BINARY)
annotation class Marker

@Marker
fun <T> render(content: @Marker () -> T): Int = -1

fun render(content: Int): Int = content

class Tag
@Marker
companion fun Tag.marked(content: @Marker () -> Int): Int = -1

class Scope<A> {
    class Nested<T> {
        @Marker
        fun <R> render(value: T, content: @Marker (T) -> R): Int = -1
    }
}
