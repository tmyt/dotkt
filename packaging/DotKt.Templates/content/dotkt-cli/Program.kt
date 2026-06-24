fun main(args: Array<String>) {
    val who = args.firstOrNull() ?: "World"
    println("Hello, $who, from DotKt — Kotlin on .NET!")
}
