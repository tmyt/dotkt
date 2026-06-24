package clr

@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
annotation class Clr(val name: String)

// A façade over a real .NET generic collection. `operator iterator()` satisfies Kotlin's for-in type check;
// the backend lowers `for (x in netList)` to GetEnumerator/MoveNext/Current (bypassing this stub).
@Clr("System.Collections.Generic.List")
class NetList<T> {
    @Clr("Add") fun add(x: T) {}
    @Clr("Count") val count: Int get() = TODO()
    operator fun iterator(): Iterator<T> = TODO()
}
