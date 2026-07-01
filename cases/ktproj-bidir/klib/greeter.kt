import Theme.Palette

// A Kotlin library. It consumes a C# type (Palette, FORWARD ProjectReference -> cslib) and is in turn
// consumed from C# (App, REVERSE ProjectReference). Both directions in one build graph.
class Greeter(val name: String) {
    fun greet(): String = "Hi, " + name + " (accent=" + Palette().Accent + ")"

    // Returns a Kotlin read-only List<String>; the @ClrTypeAlias exposes it to the C# consumer as
    // System.Collections.Generic.IReadOnlyList<string> (the runtime instance is a real .Generic.List<string>).
    fun roster(): List<String> = listOf(name + " A", name + " B", name + " C")
}
