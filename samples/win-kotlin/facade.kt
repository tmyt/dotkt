package clr

@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
annotation class Clr(val name: String)

// Façades over real Avalonia controls. Codegen maps construction/properties to the .NET types.
@Clr("Avalonia.Controls.Window")
class Window {
	@Clr("Title") var title: String = ""
	@Clr("Width") var width: Double = 0.0
	@Clr("Height") var height: Double = 0.0
	@Clr("Content") var content: Any? = null
}

@Clr("Avalonia.Controls.TextBlock")
class TextBlock {
	@Clr("Text") var text: String = ""
	@Clr("FontSize") var fontSize: Double = 0.0
}

@Clr("Kfc.Ui")
object Ui {
	// The lambda builds and returns the Window — invoked by the C# app lifecycle.
	@Clr("Run") fun run(builder: () -> Window): Unit = TODO()
}
