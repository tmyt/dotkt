import clr.Ui
import clr.Window
import clr.TextBlock

fun main() {
	Ui.run {
		val label = TextBlock()
		label.text = "This window was built entirely in Kotlin."
		label.fontSize = 22.0

		val window = Window()
		window.title = "kotlin/clr — pure Kotlin UI"
		window.width = 520.0
		window.height = 260.0
		window.content = label
		window
	}
}
