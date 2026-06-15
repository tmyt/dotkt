package clr

/** Marks a declaration as mapping to a .NET (CLR) type or member of the given name. */
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION)
annotation class Clr(val name: String)

/** Façade over the kotlin/clr UI runtime (Avalonia). Codegen maps these to `global::Kfc.Ui.*`. */
@Clr("Kfc.Ui")
object Ui {
	@Clr("Window")
	fun window(title: String, message: String, width: Int, height: Int): Unit = TODO()

	/** Window with a button; `onClick` is a Kotlin lambda the CLR binds as a delegate. */
	@Clr("WindowWithButton")
	fun window(title: String, message: String, buttonText: String, onClick: () -> Unit): Unit = TODO()
}
