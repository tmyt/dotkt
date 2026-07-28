package consumer

import Probe.Widget
import Probe.IAdder
import Probe.Bump
import kotlin.clr.byref

fun consume(): Int {
    val maybe: String? = "x"
    val widget = Widget(3)
    val definitely: String = widget.Echo("x")
    widget.Value = 5
    widget.Field = 7
    Widget.Global = 8
    widget[2] = 6
    val nested = Widget.Nested()
    val transformed = widget.Apply({ it + 2 }, 4)
    val nullable: String? = widget.MaybeNull(true)
    val required: String = widget.Required()
    var changed = 0
    val subscription = widget.Changed.subscribe { changed = it; it }
    widget.Raise(5)
    subscription.close()
    val adder: IAdder = widget
    var incremented = 10
    widget.Increment(byref(incremented))
    val shifted = widget + 4
    return widget.Add(4) + Widget.Twice(5) + definitely.length +
        widget.Value + widget.Field + Widget.Global + adder.Add(1) + widget.Identity(2) +
        widget[2] + nested.Triple(2) + transformed + widget.Bump(1) +
        (nullable?.length ?: 0) + required.length + changed + incremented + shifted.Add(0)
}

fun main() {
    println(consume())
}
