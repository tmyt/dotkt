package roundtrip.dispatchsurface

import kotlin.properties.Delegates
import kotlin.reflect.KProperty

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
    private val token: Int = 90

    companion object {
        fun marker(): Int = 91
        fun peek(host: ConstrainedGenericOwnerCompanionHost<NamedCompanionHost.Key>): Int = host.token
    }
}

fun passGenericCompanion(
    value: ConstrainedGenericOwnerCompanionHost.Companion,
): ConstrainedGenericOwnerCompanionHost.Companion = value

// A companion of a GENERIC owner cannot live inside it — CLR static storage belongs to each closed constructed type,
// so a nested carrier would give `Host<Int>` and `Host<String>` a companion each. The carrier is hoisted beside the
// owner instead, which costs it CLR nested access to the owner's private declarations; the private constructor,
// private property and private method below are the round-tripped witness that Kotlin's lexical access survives that.
class GenericSecretHost<T> private constructor(val value: T) {
    private val secret: Int = 7
    private fun hidden(): Int = 5

    companion object {
        var opened: Int = 0

        fun open(value: Int): GenericSecretHost<Int> {
            opened += 1
            return GenericSecretHost(value)
        }

        fun peek(host: GenericSecretHost<Int>): Int = host.secret + host.hidden()

        suspend fun suspendPeek(host: GenericSecretHost<Int>): Int = peek(host) + 1
    }
}

// A companion's source name is an ordinary Kotlin identifier, and other compiler types are derived from their owner's
// name in the same namespace — the star-projection existential of `Host<T>` is `Host$dotkt_star`. A companion NAMED
// `dotkt_star` on a star-projected generic owner is therefore the case where a bare `<owner>$<name>` carrier spelling
// would collide with a type the compiler mints elsewhere, and ilemit would resolve `tag` against the wrong TypeDef.
class StarProjectedCompanionHost<T>(val value: T) {
    fun tag(): Int = 7

    companion object dotkt_star {
        fun marker(): Int = 104
    }
}

fun useStarProjectedCompanionHost(host: StarProjectedCompanionHost<*>): Int = host.tag()

// The owner's own nesting path is flattened into the hoisted carrier's name, so a generic owner nested in another
// type is representable without a second naming rule.
class NestedGenericCompanionOwners {
    class Inner<T> {
        companion object Key {
            fun marker(): Int = 103
        }
    }
}

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

// A hoisted carrier reaches its owner's private state through synthesized [UnsafeAccessor] entries rather than CLR
// nesting, so every KIND of private access has to survive the move — not just a constructor, property and method.
// `lateinit` (a field read that must observe uninitialized-ness) and a delegated property (a private `$delegate`
// field plus a getValue call) are the two shapes whose access nodes differ from a plain field.
class LateinitGenericCompanionHost<T> {
    private lateinit var slot: String
    private val derived: String by lazy { "derived:" + slot }

    companion object {
        fun fill(host: LateinitGenericCompanionHost<Int>): String {
            host.slot = "filled"
            return host.slot + "/" + host.derived
        }
    }
}

// A delegated property is exposed through its real CLR get_/set_ accessor. The accessor body alone calls the
// provider's getValue/setValue and touches the private provider field. A hoisted companion therefore calls only the
// property accessor and never needs direct access to either implementation detail.
var roundtripDelegatedCounter: Int by Delegates.observable(0) { _, _, _ -> }
var roundtripNullableDelegated: String? by Delegates.observable(null) { _, _, _ -> }

class ProviderDelegateCompanionHost<T> {
    private var providedValue: Int = 106

    private operator fun getValue(
        thisRef: ProviderDelegateCompanionHost<T>,
        property: KProperty<*>,
    ): Int = providedValue

    private operator fun setValue(
        thisRef: ProviderDelegateCompanionHost<T>,
        property: KProperty<*>,
        value: Int,
    ) {
        providedValue = value
    }

    var selfProvided: Int by this

    companion object {
        fun bump(): Int {
            roundtripDelegatedCounter += 1
            return roundtripDelegatedCounter
        }

        fun updatePrivateProvider(host: ProviderDelegateCompanionHost<Int>): Int {
            host.selfProvided += 1
            return host.selfProvided
        }
    }
}

// Hoisting makes a carrier a PUBLIC TOP-LEVEL CLR type even when the Kotlin companion it implements is private or
// protected — the source visibility lives in the trusted payload, not in CLR nesting. These two owners are the
// witnesses that the reserved type still never reaches Kotlin metadata, and that a protected companion of a generic
// owner remains reachable from a subclass in another module.
class InternalGenericCompanionHost<T>(val value: T) {
    internal companion object Restricted {
        fun secret(): Int = 9
    }

    fun reveal(): Int = secret()
}

class PrivateGenericCompanionHost<T>(val value: T) {
    private companion object Hidden {
        fun secret(): Int = 4
    }

    fun reveal(): Int = secret()
}

open class ProtectedGenericCompanionHost<T> {
    protected companion object Shielded : CompanionMarker {
        override fun marker(): Int = 105
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
