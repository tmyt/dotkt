package clr

@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
annotation class Clr(val name: String)

/** Façade over a generic BCL collection. Codegen maps to `global::System.Collections.Generic.List<T>`. */
@Clr("System.Collections.Generic.List")
class DotNetList<T> {
	@Clr("Add") fun add(item: T): Unit = TODO()
	@Clr("Count") val count: Int get() = TODO()
	operator fun get(index: Int): T = TODO()
	operator fun set(index: Int, value: T): Unit = TODO()
}
