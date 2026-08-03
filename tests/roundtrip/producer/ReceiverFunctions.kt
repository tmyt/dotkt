package roundtrip.receiverfunctions

class Panel {
    var margin: Int = 0
    var padding: Int = 0
}

fun applyPanel(block: Panel.() -> Unit): Panel {
    val panel = Panel()
    panel.block()
    return panel
}

fun column(configure: Panel.() -> Unit, build: () -> Unit): Int {
    val panel = Panel()
    panel.configure()
    build()
    return panel.margin
}

class PanelBuilder(private val base: Int) {
    fun make(setup: Panel.() -> Unit): Int {
        val panel = Panel()
        panel.setup()
        return panel.margin + base
    }
    val preset: Panel.() -> Unit = { margin = 8 }
}

val defaultPanel: Panel.() -> Unit = { margin = 9 }
