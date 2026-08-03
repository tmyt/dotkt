package roundtrip.dispatchsurface

open class Animal(private val name: String) {
    open fun sound(): String = "generic"
    fun describe(): String = name + ":" + sound()
}

class Dog(name: String) : Animal(name) {
    override fun sound(): String = "woof"
}

interface Greeter {
    fun greet(name: String): String

    companion object {
        val DEFAULT: String = "Anon"

        fun create(): Greeter = object : Greeter {
            override fun greet(name: String): String = "Hi, " + name
        }
    }
}
