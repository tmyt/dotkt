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

fun overloadedReceiver(
    label: String,
    configure: Panel.() -> Unit = {},
    onClick: () -> Unit,
): Panel {
    val panel = Panel()
    panel.configure()
    onClick()
    panel.padding = label.length
    return panel
}

fun overloadedReceiver(
    label: () -> String,
    configure: Panel.() -> Unit = {},
    onClick: () -> Unit,
): Panel = overloadedReceiver(label(), configure, onClick)

fun singleReceiver(
    label: String,
    configure: Panel.() -> Unit = {},
    onClick: () -> Unit,
): Panel = overloadedReceiver(label, configure, onClick)

fun overloadedPlain(
    label: String,
    configure: (Panel) -> Unit = {},
    onClick: () -> Unit,
): Panel {
    val panel = Panel()
    configure(panel)
    onClick()
    panel.padding = label.length
    return panel
}

fun overloadedPlain(
    label: () -> String,
    configure: (Panel) -> Unit = {},
    onClick: () -> Unit,
): Panel = overloadedPlain(label(), configure, onClick)

fun <T> genericReceiver(
    label: String,
    value: T,
    configure: T.() -> Unit,
    onClick: () -> Unit,
): T {
    value.configure()
    if (label.isNotEmpty()) onClick()
    return value
}

fun <T> genericReceiver(
    label: () -> String,
    value: T,
    configure: T.() -> Unit,
    onClick: () -> Unit,
): T = genericReceiver(label(), value, configure, onClick)
