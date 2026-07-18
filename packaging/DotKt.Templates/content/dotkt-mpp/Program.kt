package dotktapp

fun main(args: Array<String>) {
    val who = args.firstOrNull() ?: "World"
    println(Greeter().greeting(who))
}
