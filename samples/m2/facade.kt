package clr

/** Marks a declaration as mapping to a .NET (CLR) type or member of the given name. */
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION)
annotation class Clr(val name: String)

/** Hand-written façade over a real BCL type. The bodies are never emitted — codegen maps calls
 *  to `global::System.Math.*`. This is the same mechanism a generated UI façade will use. */
@Clr("System.Math")
object DotNetMath {
	@Clr("Max") fun max(a: Int, b: Int): Int = TODO()
	@Clr("Min") fun min(a: Int, b: Int): Int = TODO()
	@Clr("Abs") fun abs(a: Int): Int = TODO()
}
