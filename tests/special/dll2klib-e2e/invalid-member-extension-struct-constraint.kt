import Probe.ConstrainedValue
import Probe.Widget

fun invalidMemberExtensionStructConstraint(): Int = Widget(1).ConstrainedValue("not a struct")
