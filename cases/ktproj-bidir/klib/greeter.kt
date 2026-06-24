import Theme.Palette

// A Kotlin library. It consumes a C# type (Palette, FORWARD ProjectReference -> cslib) and is in turn
// consumed from C# (App, REVERSE ProjectReference). Both directions in one build graph.
class Greeter(val name: String) {
    fun greet(): String = "Hi, " + name + " (accent=" + Palette().Accent + ")"

    // Returns a Kotlin List<String>; the C# consumer sees a real System.Collections.Generic.List<string>.
    fun roster(): List<String> = listOf(name + " A", name + " B", name + " C")
}
