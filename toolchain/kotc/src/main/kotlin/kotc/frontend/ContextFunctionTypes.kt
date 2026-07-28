@file:OptIn(org.jetbrains.kotlin.fir.declarations.DirectDeclarationsAccess::class)

package kotc.frontend

import org.jetbrains.kotlin.fir.declarations.FirDeclaration
import org.jetbrains.kotlin.fir.declarations.FirFile
import org.jetbrains.kotlin.fir.declarations.FirFunction
import org.jetbrains.kotlin.fir.declarations.FirProperty
import org.jetbrains.kotlin.fir.declarations.FirRegularClass
import org.jetbrains.kotlin.fir.types.ConeKotlinType
import org.jetbrains.kotlin.fir.types.coneTypeOrNull
import org.jetbrains.kotlin.fir.types.contextParameterNumberForFunctionType

/**
 * A Kotlin CONTEXT FUNCTION TYPE (`context(A) B.(D) -> E`) is ERASED by fir2ir: at IR level it is exactly the
 * extension function type `B.(A, D) -> E` — `@[ExtensionFunctionType] FunctionN<A, B, D, E>` — with nothing left to
 * say that `A` was a context. Kotlin considers the two the SAME type; the context-ness lives only in FIR, as the
 * `ContextFunctionTypeParams(n)` cone attribute, and on the JVM it survives a module boundary only through
 * `@kotlin.Metadata`, which DotKt does not emit.
 *
 * Losing it is not cosmetic. bir2cir stamps `[KotlinExtensionFunctionType]` from the presence of a receiver, and
 * facadegen then promotes the delegate's FIRST physical argument to the restored extension receiver — but for a
 * context function type that argument is the CONTEXT. A consumer of `fun evaluate(f: context(Box) Box.() -> Int)`
 * saw `Box.(Box) -> Int`; a bare lambda still compiled (its one ordinary parameter became the unused implicit `it`),
 * and at run `this` bound to the context instead of the receiver — a silently wrong value with no diagnostic.
 *
 * So the fact is CAPTURED HERE, while FIR still holds it, keyed by the slot's FILE PATH plus its source RANGE:
 * fir2ir copies the same PSI offsets onto the IR declaration, so the backend looks the fact back up by the emitting
 * file's path and the `IrValueParameter`/`IrFunction` offsets.
 *
 * The FILE PATH is part of the key, not decoration — source offsets are FILE-LOCAL, so two declarations at the same
 * offset in different files of one module would otherwise share an entry and one of them would be given the other's
 * arity. (`fun one(f: context(Ctx) () -> Unit)` in A.kt and `fun two(f: (Int) -> Unit)` in B.kt collide exactly when
 * their prefixes are the same length, which is neither rare nor detectable downstream.)
 *
 * BOTH ends of the range are recorded and either may match, because the two sides do not always agree on the START:
 * a FIR declaration's `source` covers its leading trivia, so a KDoc/line comment above `fun f()` moves FIR's start to
 * the comment while IR's stays on the `fun` keyword. The END is unaffected by leading trivia.
 *
 * ONE entry per slot that actually has contexts; a plain function type records nothing, so every declaration without
 * a context function type is untouched.
 *
 * KNOWN LIMIT: only the OUTERMOST type of a slot is inspected, so a context function type NESTED in another type
 * (`fun use(xs: List<context(Ctx) () -> Unit>)`) is not carried — the attribute protocol holds one arity per slot.
 */
object ClrContextFnTypes {
	/** One recorded slot: the END offset it came from, and its context arity. The end is part of the VALUE, not just
	 *  the key, so a lookup can tell its own entry from a neighbour's — see [resolve]. */
	private data class Slot(val end: Int, val count: Int)

	/** "<p|r>|<file path>|<offset>" -> the slot recorded at that offset. Entries live for ONE pipeline execution;
	 *  [reset] clears them before a module's sessions are walked. */
	private val byKey = java.util.concurrent.ConcurrentHashMap<String, Slot>()

	/** Keys two DIFFERENT slots wrote with different contents. Poisoning is MONOTONIC — a poisoned key never holds a
	 *  value again, so a later write (a generated accessor re-recording its property's range, say) cannot revive it —
	 *  and [resolve] consults it before trusting any value. */
	private val poisoned = java.util.concurrent.ConcurrentHashMap.newKeySet<String>()

	/** RETURN-position slots are keyed in their own namespace (`r|` vs `p|`), so a parameter and a declaration that
	 *  share an offset cannot share an entry — `fun f(g: context(A) () -> Int): context(B) () -> Int` on one line is
	 *  two positions whose ranges overlap, and the tag is what keeps them apart. */
	private fun key(file: String, offset: Int, ret: Boolean) = (if (ret) "r|" else "p|") + file + "|" + offset

	private fun put(file: String, offset: Int, ret: Boolean, slot: Slot) {
		if (offset < 0 || slot.count <= 0) return
		val k = key(file, offset, ret)
		if (k in poisoned) return
		val prior = byKey.putIfAbsent(k, slot)
		if (prior != null && prior != slot) {
			// Two slots meet at this offset. Source ranges are HALF-OPEN, so a declaration's end offset IS its neighbour's
			// start offset whenever no character separates them — `fun a(): context(A) () -> Unit { … }val b:
			// context(B, C) () -> Unit = {}` is legal Kotlin and makes exactly that happen. The key stops answering; each
			// slot still has its OTHER endpoint, and `resolve`'s end check keeps it from taking the neighbour's value.
			byKey.remove(k)
			poisoned.add(k)
		}
	}

	// Keyed by the slot's END offset ALONE. The two sides disagree about the START — a FIR declaration's `source`
	// covers its leading trivia, so a KDoc/line comment above `fun f()` moves FIR's start to the comment while IR's
	// stays on the `fun` keyword — and they always agree on the END, so the end is the only offset worth keying by.
	//
	// Recording the start as a SECOND key (an earlier shape of this) bought nothing for lookups and created the one
	// way two slots could share a key: source ranges are HALF-OPEN, so a declaration's end offset IS its neighbour's
	// start offset whenever no character separates them, and `fun a(): context(A) () -> Unit { … }val b: context(B, C)
	// () -> Unit = {}` is legal Kotlin. With the start key gone, a slot's key is its own end and nothing else writes
	// there — except a declaration and its own generated accessor, which share a range AND an arity.
	fun record(file: String, start: Int, end: Int, contextCount: Int) = put(file, end, false, Slot(end, contextCount))

	fun recordReturn(file: String, start: Int, end: Int, contextCount: Int) = put(file, end, true, Slot(end, contextCount))

	/** Clear every recorded fact. `ClrContextFnTypes` is an object, so its map would otherwise live as long as the
	 *  JVM: a HOSTED kotc (a Gradle-daemon-style long-lived process) could read an entry a PREVIOUS compilation left
	 *  behind whenever a file's offsets stay stable while its context-function-type arity changes. That is a latent
	 *  hazard rather than a reproducing bug — today's launcher execs a fresh JVM per invocation and each pipeline runs
	 *  once — and this closes it by construction for whenever something does host the compiler in-process. */
	fun reset() {
		byKey.clear()
		poisoned.clear()
	}

	/** 0 when this slot is not a context function type — the overwhelmingly common case. */
	fun contextCountAt(file: String?, start: Int, end: Int): Int = resolve(file, start, end, ret = false)

	fun returnContextCountAt(file: String?, start: Int, end: Int): Int = resolve(file, start, end, ret = true)

	/** Look this slot's END offset up in its own (`p|`/`r|`) namespace. `start` is accepted for symmetry with the
	 *  recording side and deliberately unused — see [record] for why the end is the only offset keyed by.
	 *
	 *  A poisoned key yields NO fact rather than a diagnostic. Two slots that end together cannot be told apart, and
	 *  the alternatives are: hand over one of the two arities (a wrong restored type at a consumer), stop the compile
	 *  (a crash on source the frontend accepted, which this project does not do), or carry nothing. Carrying nothing
	 *  degrades that slot to a plain function type — the same outcome as every other position the carrier does not
	 *  reach — so that is what it does. With the declaration-only walk in [capture] no two recorded slots are
	 *  co-terminal, so this is not expected to arise; it is handled rather than assumed away. */
	private fun resolve(file: String?, @Suppress("UNUSED_PARAMETER") start: Int, end: Int, ret: Boolean): Int {
		if (file == null) return 0
		return byKey[key(file, end, ret)]?.count ?: 0
	}

	/** Record every context-function-type slot the module DECLARES. Called from each frontend pipeline right after
	 *  `resolveAndCheckFir`, i.e. after types are resolved and before fir2ir runs.
	 *
	 *  Walks the DECLARATION structure only — classes, their members, functions and their value parameters, properties
	 *  and their accessors — and never descends into a body, an initializer or a parameter's default value. Two
	 *  reasons, and the first is a correctness one:
	 *
	 *  1. A callable nested in an expression body ENDS where its enclosing declaration ends, so both would key on the
	 *     same offset with different arities. `fun f(block: context(A) (context(B, C) () -> Unit) -> Unit = { }) {}`
	 *     and `val p: context(A) () -> context(B, C) () -> Unit = { { } }` both do this. Walking declarations only
	 *     means no two recorded slots are co-terminal, so the shared-key case does not arise at all.
	 *  2. Nothing inside a body is ever looked up: the backend reads this table for a DECLARATION's parameters and
	 *     return type, which is exactly the surface a consuming module restores. A lambda inside a body has no slot
	 *     of its own to carry.
	 */
	fun capture(files: List<FirFile>) {
		for (f in files) {
			val path = f.sourceFile?.path ?: continue
			for (d in f.declarations) walkDeclaration(path, d)
		}
	}

	private fun walkDeclaration(path: String, d: FirDeclaration) {
		when (d) {
			is FirRegularClass -> for (m in d.declarations) walkDeclaration(path, m)
			is FirFunction -> {
				// A function's RETURN, then each of its value PARAMETERS (a constructor's included).
				d.source?.let { recordReturn(path, it.startOffset, it.endOffset, ctxCount(d.returnTypeRef.coneTypeOrNull)) }
				for (p in d.valueParameters)
					p.source?.let { record(path, it.startOffset, it.endOffset, ctxCount(p.returnTypeRef.coneTypeOrNull)) }
			}
			is FirProperty -> {
				// The property's own TYPE answers for its getter (the backend falls back to the property's range).
				d.source?.let { recordReturn(path, it.startOffset, it.endOffset, ctxCount(d.returnTypeRef.coneTypeOrNull)) }
				d.getter?.let { walkDeclaration(path, it) }
				d.setter?.let { walkDeclaration(path, it) }
			}
			else -> {}
		}
	}

	private fun ctxCount(t: ConeKotlinType?): Int = t?.contextParameterNumberForFunctionType ?: 0
}
