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
 * projections of facadegen's metadata rather than an injector-populated mutable map. Stage 3 (2026-07-05) removed
 * `ClrTopLevelRegistry` the same way: a restored top-level function/extension-property's .NET file-facade class is
 * read off the resolved IR `CallableId` (`kotc.frontend.clrInjectedTopLevelFileClass` /
 * `clrInjectedTopLevelPropFileClass`), so the name-keyed candidate list + receiver-discriminator kludge is gone. Only
 * the event channel below is interop-no-registry stage 4 and is DELIBERATELY untouched here.
 */

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
