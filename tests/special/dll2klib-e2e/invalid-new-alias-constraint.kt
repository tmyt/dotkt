import Probe.FreshConstraintBox

fun main() {
    // kotlin.String is physically System.String, whose public surface has no parameterless constructor.
    FreshConstraintBox<String>()
}
