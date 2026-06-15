package clr

@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
annotation class Clr(val name: String)

/** Façade over a real BCL instance type. Codegen maps these to `global::System.Text.StringBuilder`. */
@Clr("System.Text.StringBuilder")
class StringBuilder {
	@Clr("Append") fun append(s: String): StringBuilder = TODO()
	@Clr("Append") fun append(n: Int): StringBuilder = TODO()
	@Clr("ToString") override fun toString(): String = TODO()
	@Clr("Length") val length: Int get() = TODO()
}
