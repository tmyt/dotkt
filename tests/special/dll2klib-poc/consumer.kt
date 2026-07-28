package consumer

import Probe.Widget
import Probe.IAdder

fun consume(): Int {
    val maybe: String? = "x"
    // Both directions are required: a platform parameter accepts nullable input,
    // while a platform return can be consumed as non-null.
    val widget = Widget(3)
    val definitely: String = widget.Echo(maybe)
    widget.Value = 5
    widget.Field = 7
    Widget.Global = 8
    val adder: IAdder = widget
    return widget.Add(4) + Widget.Twice(5) + definitely.length +
        widget.Value + widget.Field + Widget.Global + adder.Add(1) + widget.Identity(2)
}

fun main() {
    println(consume())
}
