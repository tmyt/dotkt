package consumer

import Probe.Widget
import Probe.WidgetExtensions
import Probe.IAdder
import Probe.Bump
import Probe.IVisibleControl
import Probe.VisibilityProbe
import Probe.DefaultCarrier1
import Probe.DefaultCarrier2
import Probe.IPublicDefaultSlot
import Probe.GenericDefaultCarrier
import Probe.ExternalDefaultCarrier
import Probe.ExplicitDefaultCarrier
import Probe.IPublicGenericDefaultSlot
import Probe.Contracts.IVisibleGeneric
import Probe.Contracts.IExternalDefaultSlot
import GlobalWidgetExtensions
import GlobalBump
import Probe.ConstraintBox
import Probe.ConstraintKind
import Probe.EnumConstraintBox
import Probe.FreshConstraintBox
import Probe.GoodConstraintSink
import Probe.ReferenceConstraintBox
import Probe.StructConstraintBox
import kotlin.clr.byref

class LocalDefaultConstraintValue {
    val value: Int = 16
}

class DefaultCarrierSubclass1 : DefaultCarrier1()

class DefaultCarrierSubclass2 : DefaultCarrier2()

class GenericDefaultCarrierSubclass : GenericDefaultCarrier()

class ExternalDefaultCarrierSubclass : ExternalDefaultCarrier()

class ExplicitDefaultCarrierSubclass : ExplicitDefaultCarrier()

fun consume(): Int {
    val maybe: String? = "x"
    val widget = Widget(3)
    val definitely: String = widget.Echo("x")
    widget.Value = 5
    widget.Inherited = 11
    widget.Field = 7
    Widget.Global = 8
    widget[2] = 6
    val nested = Widget.Nested()
    val transformed = widget.Apply({ it + 2 }, 4)
    val externalTransformed = widget.ApplyExternal({ it * 3 }, 4)
    val externalGenericTransformed = widget.ApplyExternalGeneric({ it + 5 }, 4)
    val nullable: String? = widget.MaybeNull(true)
    val required: String = widget.Required()
    var changed = 0
    val subscription = widget.Changed.subscribe { changed = it; it }
    widget.Raise(5)
    subscription.close()
    subscription.close()
    widget.Raise(99)
    val adder: IAdder = widget
    var incremented = 10
    widget.Increment(byref(incremented))
    val shifted = widget + 4
    val staticBump = WidgetExtensions.Bump(widget, 1)
    val globalExtensionBump = widget.GlobalBump(1)
    val globalStaticBump = GlobalWidgetExtensions.GlobalBump(widget, 1)
    val visibility = VisibilityProbe()
    val visibleControl: IVisibleControl = visibility
    val visibleGeneric: IVisibleGeneric<String> = visibility
    val defaultCarrier1: IPublicDefaultSlot = DefaultCarrierSubclass1()
    val defaultCarrier2: IPublicDefaultSlot = DefaultCarrierSubclass2()
    defaultCarrier1.M()
    defaultCarrier2.M()
    val genericDefaultCarrier: IPublicGenericDefaultSlot<String> = GenericDefaultCarrierSubclass()
    genericDefaultCarrier.Echo("ok")
    val externalDefaultCarrier: IExternalDefaultSlot = ExternalDefaultCarrierSubclass()
    externalDefaultCarrier.Value()
    val explicitDefaultCarrier: IExternalDefaultSlot = ExplicitDefaultCarrierSubclass()
    explicitDefaultCarrier.Value()
    val genericConstraints = ConstraintBox<GoodConstraintSink>().Value +
        StructConstraintBox<Int>().Value +
        EnumConstraintBox<ConstraintKind>().Value +
        ReferenceConstraintBox<String>().Value +
        FreshConstraintBox<LocalDefaultConstraintValue>().Create().value
    return widget.Add(4) + Widget.Twice(5) + definitely.length +
        widget.Value + widget.Inherited + widget.Field + Widget.Global + adder.Add(1) + widget.Identity(2) +
        widget[2] + nested.Triple(2) + transformed + widget.Bump(1) +
        externalTransformed + externalGenericTransformed + staticBump + globalExtensionBump + globalStaticBump +
        (nullable?.length ?: 0) + required.length + changed + incremented + shifted.Add(0) +
        visibility.Read() + visibleControl.Read() + (if (visibleGeneric === visibility) 1 else 0) +
        genericConstraints
}

fun main() {
    println(consume())
}
