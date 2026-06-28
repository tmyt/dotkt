package kotc

/**
 * S5 bridge between the FIR type-injection frontend extension and the backend codegen.
 *
 * The frontend extension synthesizes .NET types into FIR *without* annotations (synthesizing FIR
 * annotations that survive Fir2Ir is the brittle part). Instead it records `Kotlin FQN -> .NET type`
 * here, and the backend's `clrName` consults this map as a fallback after `@Clr`. Net effect: a
 * synthesized `clrgen.Math` maps to `System.Math` exactly as a hand-written `@Clr("System.Math")`
 * would — but with no façade `.kt` file. Single JVM process, so a static registry is the simplest
 * correct channel between the two compiler phases.
 */
object ClrTypeRegistry {
	private val typeNames = HashMap<String, String>()
	// Per-MEMBER BCL name (ref/runtime split, app-emit member substitution): key = the member's Kotlin fqn
	// (`kotlin.collections.Collection.size`) -> its BCL member name (`Count`). Populated from the `[clr.Clr]` carried in
	// the injection meta when an app references the ref stdlib; the backend's clrName consults it for an injected member.
	private val memberNames = HashMap<String, String>()

	fun register(kotlinFqn: String, dotNetName: String) { typeNames[kotlinFqn] = dotNetName }

	/** The .NET type name for a synthesized Kotlin class FQN, or null if not injected. */
	fun dotNetName(kotlinFqn: String): String? = typeNames[kotlinFqn]

	fun registerMember(memberFqn: String, bclName: String) { memberNames[memberFqn] = bclName }

	/** The BCL member name for an injected Kotlin member FQN (e.g. `...Collection.size` -> `Count`), or null. */
	fun memberClrName(memberFqn: String): String? = memberNames[memberFqn]
}

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

	fun register(fqn: String, fileClassDotNet: String, recvDisc: String?, suspend: Boolean) { funs.getOrPut(fqn) { ArrayList() }.add(Triple(fileClassDotNet, recvDisc, suspend)) }
	fun registerProp(fqn: String, fileClassDotNet: String) { props[fqn] = fileClassDotNet }

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
