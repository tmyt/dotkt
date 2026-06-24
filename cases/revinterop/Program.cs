class Program {
    static void Main() {
        var g = new Greeter("World");          // Kotlin class, from a referenced assembly
        System.Console.WriteLine(g.greet());
        System.Console.WriteLine(LibKt.add(2, 3));   // Kotlin top-level fun
    }
}
