// Issue #24: a property OVERRIDE (`override val message`) on a class extending a @ClrTypeAlias stdlib base
// (kotlin.Exception -> System.Exception) must be DISPATCHED. `message` is bound by @ClrProperty(READ,"Message")
// on kotlin.Throwable — NOT @ClrIntrinsic — so DeclarationRename must wire the override accessor `get_message`
// to the System.Exception.get_Message virtual slot (rename + clrOverride) so ilemit's DefineMethodOverride reuses
// the base slot. Before the fix the accessor emitted a fresh newslot and every read bound the base value ("boom").

class MyEx : Exception("boom") {
	override val message: String get() = "overridden"
}

fun main() {
	val e = MyEx()
	println(e.message)                  // overridden — direct receiver

	val base: Exception = e             // through the @ClrTypeAlias base static type -> virtual dispatch on the BCL slot
	println(base.message)               // overridden

	try {
		throw MyEx()                    // the throw/catch path reads System.Exception.Message
	} catch (ex: Exception) {
		println(ex.message)             // overridden
	}
}
