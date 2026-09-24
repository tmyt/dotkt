package roundtrip.projectedclrconstruction

class TextOrder : Comparable<String> {
    override fun compareTo(other: String): Int = other.length
}

fun createProjected(): System.Collections.Generic.List<Comparable<in String>> {
    val values = System.Collections.Generic.List<Comparable<in String>>(3)
    values.Add(TextOrder())
    return values
}

fun consumeClosed(values: System.Collections.Generic.List<Comparable<String>>): Int =
    values[0].compareTo("value")

fun createStarred(): System.Collections.Generic.List<Comparable<*>> {
    val values = System.Collections.Generic.List<Comparable<*>>()
    values.Add(7)
    values.Add("text")
    return values
}

fun createProjectedMutable(): System.Collections.Generic.List<MutableList<out String>> {
    val values = System.Collections.Generic.List<MutableList<out String>>()
    values.Add(mutableListOf("nested"))
    return values
}
