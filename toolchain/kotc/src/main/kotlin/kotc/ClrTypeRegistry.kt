package kotc

/*
 * The FIR type-injection frontend extension (`ClrTypeInjector`) synthesizes .NET types into FIR *without*
 * annotations (synthesizing FIR annotations that survive Fir2Ir is the brittle part), and the backend
 * (`BirEmitter`) recovers each injected symbol's CLR facts from its RESOLVED IR identity.
 *
 * A2 keystone — interop-no-registry (2026-07-05): the former `ClrTypeRegistry` name-keyed side-table is GONE.
 * Its TYPE-name channel (stage 1) is read off the injected type's IR `ClassId`
 * (`kotc.frontend.clrInjectedDotNetName`) and its per-MEMBER slot-name channel (stage 2) off the injected
 * member's IR `CallableId` (`kotc.frontend.clrInjectedMemberName`) — both structural, resolved identities,
 * projections of facadegen's metadata rather than an injector-populated mutable map. The top-level and event
 * channels below are interop-no-registry stages 3-4 and are DELIBERATELY untouched here.
 */

/**
 * DotKt round-trip: a Kotlin top-level function compiles to a static method of a `<File>Kt` facade class. When
 * such an assembly is consumed AS KOTLIN, the FIR injector restores those statics as top-level functions (read
 * from a [KotlinFile]-marked class) and records here that a call to one means a .NET STATIC call to the file class.
 * The backend consults this in `call()` (and the suspend path) and emits `LibKt.greet(...)`.
 */
object ClrTopLevelRegistry {
	// key = the restored top-level fun's FQN ("greet", "kotlin.collections.reversed")  ->  a LIST of (.NET file class,
	// receiver discriminator, suspend?). A name like reversed/toList lives in MANY file classes (_CollectionsKt/_ArraysKt/
	// _UArraysKt/_StringsKt) -> disambiguate the file class by the call's RECEIVER type (else the last-registered overload
	// wins and ilemit's ResolveGenericMethod gets 0 candidates).
	private val funs = HashMap<String, MutableList<Triple<String, String?, Boolean>>>()
	// key = a restored top-level EXTENSION property's FQN  ->  .NET file class (its get_/set_<name> static accessors).
	private val props = HashMap<String, String>()

	fun register(fqn: String, fileClassDotNet: String, recvDisc: String?, suspend: Boolean) { funs.getOrPut(fqn) { ArrayList() }.add(Triple(stripClrFileClass(fileClassDotNet), recvDisc, suspend)) }
	fun registerProp(fqn: String, fileClassDotNet: String) { props[fqn] = stripClrFileClass(fileClassDotNet) }
	// Platform-actual files `<Common>Clr.kt` emit their actuals into the COMMON file class `<Common>Kt` -- ilemit/the rt
	// strip the `Clr` suffix (BirEmitter.fileClassName). The registry's fileClass comes from the K2 frontend jar, which
	// does NOT strip, so a non-inline top-level call would reference `<Common>ClrKt` -- never emitted by the rt, giving
	// `cannot resolve .NET type ...ClrKt`. Strip here to match the rt. Mirrors fileClassName's `stem.endsWith("Clr")`.
	private fun stripClrFileClass(fc: String): String {
		val dot = fc.lastIndexOf('.'); val simple = if (dot >= 0) fc.substring(dot + 1) else fc
		return if (simple.endsWith("ClrKt")) (if (dot >= 0) fc.substring(0, dot + 1) else "") + simple.removeSuffix("ClrKt") + "Kt" else fc
	}

	/** (.NET file class, isSuspend) for an injected top-level fun FQN matching the receiver discriminator, or null. */
	fun lookup(fqn: String?, recvDisc: String? = null): Pair<String, Boolean>? = fqn?.let { funs[it] }?.let { list ->
		val e = list.firstOrNull { it.second == recvDisc } ?: list.firstOrNull { it.second == null } ?: list.first()
		e.first to e.third
	}
	/** .NET file class for an injected top-level extension-property FQN (accessors are get_/set_<name>), or null. */
	fun lookupProp(fqn: String?): String? = fqn?.let { props[it] }
}

/**
 * I4: .NET events have no Kotlin syntax, so the FIR injector synthesizes `add_<E>`/`remove_<E>`
 * methods and records here that a call to one means `receiver.<E> += handler` / `-= handler`. The
 * backend consults this in `genCallInner` and emits the C# event-subscription operator.
 */
object ClrEventRegistry {
	// key = "<owner Kotlin FQN>#<methodName>"  ->  (eventName, "+=" | "-=")
	private val ops = HashMap<String, Pair<String, String>>()

	fun register(ownerFqn: String, methodName: String, eventName: String, op: String) {
		ops["$ownerFqn#$methodName"] = eventName to op
	}

	/** (eventName, op) for an injected `add_`/`remove_` method call, or null. */
	fun lookup(ownerFqn: String?, methodName: String): Pair<String, String>? =
		ownerFqn?.let { ops["$it#$methodName"] }
}
