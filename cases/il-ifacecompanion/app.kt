// #83: an interface's PLAIN companion object flattens to the interface's own statics — a CLR interface
// legally carries static fields (run in its .cctor) + static methods. Shape mirrors kotlinx.coroutines'
// `SharingStarted.Eagerly`/`Lazily` (interface companion `val`s built by a ctor call) and the named
// `Channel.Factory` const/val cluster. Before the fix kotc dropped the companion members and ilemit
// reported `field SharingStarted.Eagerly not found` at the access site.
interface SharingStarted {
	fun tag(): Int
	companion object {
		val Eagerly: SharingStarted = StartedEagerly()
		val Lazily: SharingStarted = StartedLazily()
		const val VERSION: Int = 3
		fun describe(s: SharingStarted): Int = s.tag() + VERSION
	}
}
class StartedEagerly : SharingStarted { override fun tag() = 1 }
class StartedLazily : SharingStarted { override fun tag() = 2 }

// A NAMED companion (`Factory`) with a non-const `val` initialized by a call + a `const val`, accessed
// unqualified after `import ...Channel.Factory.CHANNEL_DEFAULT_CAPACITY` would in the real port — here
// accessed qualified. Non-interface path (already worked), kept as a co-located non-regression.
class Channel {
	companion object Factory {
		const val UNLIMITED: Int = 2147483647
		val CHANNEL_DEFAULT_CAPACITY: Int = computeCap()
	}
}
fun computeCap(): Int = 64

fun main() {
	println(SharingStarted.Eagerly.tag())
	println(SharingStarted.Lazily.tag())
	println(SharingStarted.VERSION)
	println(SharingStarted.describe(SharingStarted.Eagerly))
	println(Channel.CHANNEL_DEFAULT_CAPACITY)
	println(Channel.UNLIMITED)
}
