import clr.Ui

fun main() {
	var clicks = 0
	Ui.window("Kotlin/CLR", "Click the button — handled in Kotlin", "Click me") {
		clicks = clicks + 1
		println("button handler ran in Kotlin; clicks = $clicks")
	}
}
