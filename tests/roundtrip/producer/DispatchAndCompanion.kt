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

interface CompanionMarker {
    fun marker(): Int
}

fun markerValue(value: CompanionMarker): Int = value.marker()

class NamedCompanionHost {
    companion object Key : CompanionMarker {
        val token: Int = 6
        override fun marker(): Int = 42
        fun <T> id(value: T): T = value
        suspend fun suspendMarker(value: Int): Int = value + 41
    }
}

class DefaultCompanionHost {
    companion object {
        val token: Int = 12
        fun marker(): Int = 24
        fun defaulted(value: Int = token): Int = value + 1
        inline fun mapped(transform: (Int) -> Int): Int = transform(token)
    }
}

enum class EnumCompanionHost {
    ENTRY;

    companion object Key {
        fun marker(): Int = 73
    }
}

fun localDefaultCompanionUse(): Int {
    val token = DefaultCompanionHost.Companion::token
    return DefaultCompanionHost.Companion.defaulted() +
        DefaultCompanionHost.Companion.mapped { it + 1 } +
        token.get()
}

class ConstrainedGenericOwnerCompanionHost<T : CompanionMarker> {
    companion object {
        fun marker(): Int = 91
    }
}

fun passGenericCompanion(
    value: ConstrainedGenericOwnerCompanionHost.Companion,
): ConstrainedGenericOwnerCompanionHost.Companion = value

class NestedCompanionOwners {
    interface NestedInterface {
        companion object {
            fun marker(): Int = 101
        }
    }

    enum class NestedEnum {
        ENTRY;

        companion object {
            fun marker(): Int = 102
        }
    }
}

class PrivateCompanionHost {
    private companion object Secret {
        fun hidden(): Int = 2
        private fun privateHidden(): Int = 3
    }

    fun reveal(): Int = hidden() + privateHidden()
}

open class ProtectedCompanionHost {
    protected companion object Shield : CompanionMarker {
        override fun marker(): Int = 10
        val token: Int = 11
        private fun privateSecret(): Int = 12
        suspend fun suspendMarker(value: Int): Int = value + 19
    }
}
