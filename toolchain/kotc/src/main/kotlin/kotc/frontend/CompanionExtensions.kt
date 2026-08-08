@file:OptIn(org.jetbrains.kotlin.fir.declarations.DirectDeclarationsAccess::class)

package kotc.frontend

import kotc.bir.TypeNode
import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.fir.declarations.FirCallableDeclaration
import org.jetbrains.kotlin.fir.declarations.FirDeclaration
import org.jetbrains.kotlin.fir.declarations.FirFile
import org.jetbrains.kotlin.fir.declarations.FirProperty
import org.jetbrains.kotlin.fir.declarations.utils.isCompanionExtension
import org.jetbrains.kotlin.fir.resolve.fullyExpandedType
import org.jetbrains.kotlin.fir.types.ConeClassLikeType
import org.jetbrains.kotlin.fir.types.ConeDefinitelyNotNullType
import org.jetbrains.kotlin.fir.types.ConeFlexibleType
import org.jetbrains.kotlin.fir.types.ConeKotlinType
import org.jetbrains.kotlin.fir.types.ConeTypeParameterType
import org.jetbrains.kotlin.fir.types.coneTypeOrNull

/**
 * Kotlin 2.4 `companion fun C.foo()` / `companion val C.bar` — a COMPANION EXTENSION: a receiverless static
 * declaration that is nevertheless semantically associated with the type `C`, so it is written and called as
 * `C.foo()` rather than through an instance.
 *
 * fir2ir ERASES that association. `Fir2IrCallableDeclarationsGenerator` skips the extension-receiver parameter
 * for a companion extension, so at IR level the declaration is an ordinary receiverless top-level function and
 * NOTHING says it belongs to `C`. Same-module compilation survives that (the frontend already resolved the call
 * site to this declaration), but the emitted assembly then carries no association either — and a second module,
 * which must rediscover the declaration from metadata, cannot resolve `C.foo(...)` at all.
 *
 * So the fact is CAPTURED HERE, while FIR still holds it, keyed by the declaration's FILE PATH plus its source
 * END offset — exactly the key [ClrContextFnTypes] uses, and for the same reason: fir2ir copies the PSI offsets
 * onto the IR declaration, FIR and IR can disagree about the START (leading trivia moves FIR's start) but always
 * agree about the END, and the path is part of the key because offsets are file-local.
 *
 * The value is the receiver type already rendered as BIR type JSON. Rendering here rather than in the backend
 * keeps the ONE Cone-typed step next to the FIR it reads; the backend splices an opaque string.
 */
object ClrCompanionExtensions {
	/** "<file path>|<end offset>" -> the receiver type of the companion extension declared there, as BIR type JSON. */
	private val byKey = java.util.concurrent.ConcurrentHashMap<String, String>()

	/** Keys two different declarations wrote with different contents; monotonic, exactly as in [ClrContextFnTypes]. */
	private val poisoned = java.util.concurrent.ConcurrentHashMap.newKeySet<String>()

	private fun key(file: String, offset: Int) = "$file|$offset"

	private fun put(file: String, offset: Int, typeJson: String) {
		if (offset < 0) return
		val k = key(file, offset)
		if (k in poisoned) return
		val prior = byKey.putIfAbsent(k, typeJson)
		if (prior != null && prior != typeJson) {
			byKey.remove(k)
			poisoned.add(k)
		}
	}

	/** Clear every recorded fact, so a hosted (long-lived) kotc cannot read a previous compilation's entry. */
	fun reset() {
		byKey.clear()
		poisoned.clear()
	}

	/** The BIR type JSON of the companion-extension receiver declared at this slot, or null when there is none. */
	fun receiverTypeJsonAt(file: String?, end: Int): String? {
		if (file == null || end < 0) return null
		return byKey[key(file, end)]
	}

	/**
	 * Record every companion extension the module DECLARES. Called from each frontend pipeline right after
	 * `resolveAndCheckFir`, i.e. after types are resolved and before fir2ir runs.
	 *
	 * Only TOP-LEVEL declarations are walked: `companion` on a member is a companion-BLOCK member (a genuine static
	 * of its class, which IR still reports faithfully and which needs no side table), and a companion block may not
	 * declare an extension at all (COMPANION_BLOCK_MEMBER_EXTENSION).
	 *
	 * A property records its own range AND each explicitly written accessor's range, because the backend looks the
	 * fact up from whichever IR declaration it is emitting — the static field comes from the property, the
	 * `get_`/`set_` methods from the accessors.
	 */
	fun capture(session: FirSession, files: List<FirFile>) {
		for (f in files) {
			val path = f.sourceFile?.path ?: continue
			for (d in f.declarations) record(session, path, d)
		}
	}

	private fun record(session: FirSession, path: String, d: FirDeclaration) {
		if (d !is FirCallableDeclaration || !d.isCompanionExtension) return
		val receiver = d.receiverParameter?.typeRef?.coneTypeOrNull ?: return
		val json = associatedType(session, receiver).toJson()
		d.source?.let { put(path, it.endOffset, json) }
		if (d is FirProperty) {
			d.getter?.source?.let { put(path, it.endOffset, json) }
			d.setter?.source?.let { put(path, it.endOffset, json) }
		}
	}

	/**
	 * The type a companion extension is associated with: its receiver's BARE CLASSIFIER.
	 *
	 * Bare is the whole story here, not a simplification. `FirCompanionExtensionChecker` rejects every other shape a
	 * receiver could take — type arguments (COMPANION_EXTENSION_RECEIVER_WITH_TYPE_ARGUMENTS), a type parameter
	 * (COMPANION_EXTENSION_RECEIVER_IS_TYPE_PARAMETER), a nullable receiver (COMPANION_EXTENSION_NULLABLE_RECEIVER),
	 * an object (COMPANION_EXTENSION_RECEIVER_IS_OBJECT) and `dynamic` — so what survives resolution is a classifier
	 * and nothing else. `companion fun C.f()` on a generic `class C<T>` is written without arguments and called as
	 * `C.f()`, which is exactly a bare classifier both ways.
	 *
	 * A TYPEALIAS receiver is legal and denotes the class it expands to (`typealias TA = C<String>`;
	 * `companion fun TA.f()` is callable as `C.f()` and as `TA.f()`), so the alias is expanded and its arguments —
	 * which the source could not have written itself — drop with everything else.
	 *
	 * The unreachable shapes are still handled rather than asserted away: a receiver the checker rejected reaches
	 * fir2ir only in an already-erroneous compilation, and carrying its printable identity is a better outcome there
	 * than a backend failure on frontend-rejected source.
	 */
	private fun associatedType(session: FirSession, type: ConeKotlinType): TypeNode = when (type) {
		is ConeFlexibleType -> associatedType(session, type.lowerBound)
		is ConeDefinitelyNotNullType -> associatedType(session, type.original)
		is ConeClassLikeType -> TypeNode.Fqn(type.fullyExpandedType(session).lookupTag.classId.asFqNameString())
		is ConeTypeParameterType -> TypeNode.Fqn(type.lookupTag.typeParameterSymbol.name.asString())
		else -> TypeNode.Fqn(type.toString())
	}
}
