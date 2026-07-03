// Mirror the cold-core coroutine shape that surfaced the bug:
//   an interface declares a `val`, an abstract class overrides it (filling the interface slot
//   with its OWN concrete backing), and a concrete subclass does NOT re-override the property.
// Before the fix, the abstract class's `get_ctx` accessor emitted as a fresh NewSlot instead of
// filling the interface/base slot, so the concrete subclass left the abstract slot unfilled and
// the type failed to load (TypeLoadException) at class load.

interface HasCtx {
	val ctx: Int
}

// abstract class overriding the interface property with a stored value
abstract class Base(override val ctx: Int) : HasCtx {
	abstract fun run(): Int
}

// concrete subclass that does NOT re-override `ctx` — it must inherit Base's filled slot
class Impl(ctx: Int) : Base(ctx) {
	override fun run(): Int = ctx * 2
}

// also cover: abstract class with an abstract `val`, concrete subclass overriding it
abstract class AbstractHolder {
	abstract val value: Int
}
class Holder(override val value: Int) : AbstractHolder()

fun main() {
	val h: HasCtx = Impl(21)
	println(h.ctx)          // 21
	println((h as Base).run())  // 42
	val a: AbstractHolder = Holder(7)
	println(a.value)        // 7
}
