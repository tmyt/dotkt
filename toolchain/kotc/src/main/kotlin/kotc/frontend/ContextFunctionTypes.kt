package kotc.frontend

import org.jetbrains.kotlin.fir.FirElement
import org.jetbrains.kotlin.fir.declarations.FirFile
import org.jetbrains.kotlin.fir.declarations.FirCallableDeclaration
import org.jetbrains.kotlin.fir.declarations.FirValueParameter
import org.jetbrains.kotlin.fir.types.ConeKotlinType
import org.jetbrains.kotlin.fir.types.coneTypeOrNull
import org.jetbrains.kotlin.fir.types.contextParameterNumberForFunctionType
import org.jetbrains.kotlin.fir.visitors.FirVisitorVoid

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
	/** "<file path>|<offset>" -> the number of LEADING physical arguments of that function type that are contexts. */
	private val byKey = java.util.concurrent.ConcurrentHashMap<String, Int>()

	/** RETURN-position slots are keyed in their own namespace, so a declaration and its own first parameter cannot
	 *  share an entry through a coincident offset. */
	private fun key(file: String, offset: Int, ret: Boolean) = (if (ret) "r|" else "p|") + file + "|" + offset

	private fun put(file: String, offset: Int, ret: Boolean, contextCount: Int) {
		if (offset >= 0 && contextCount > 0) byKey[key(file, offset, ret)] = contextCount
	}

	fun record(file: String, start: Int, end: Int, contextCount: Int) {
		put(file, start, false, contextCount); put(file, end, false, contextCount)
	}

	fun recordReturn(file: String, start: Int, end: Int, contextCount: Int) {
		put(file, start, true, contextCount); put(file, end, true, contextCount)
	}

	/** 0 when this slot is not a context function type — the overwhelmingly common case. */
	fun contextCountAt(file: String?, start: Int, end: Int): Int =
		if (file == null) 0 else byKey[key(file, start, false)] ?: byKey[key(file, end, false)] ?: 0

	fun returnContextCountAt(file: String?, start: Int, end: Int): Int =
		if (file == null) 0 else byKey[key(file, start, true)] ?: byKey[key(file, end, true)] ?: 0

	/** Walk the RESOLVED FIR of one module and record every context-function-type slot it declares. Called from the
	 *  frontend pipeline right after `resolveAndCheckFir`, i.e. after types are resolved and before fir2ir runs. */
	fun capture(files: List<FirFile>) {
		for (f in files) {
			val path = f.sourceFile?.path ?: continue
			val visitor = object : FirVisitorVoid() {
				override fun visitElement(element: FirElement) {
					when (element) {
						// A VALUE PARAMETER's own source range is the key: fir2ir gives the IrValueParameter the same one.
						is FirValueParameter -> element.source?.let {
							record(path, it.startOffset, it.endOffset, ctxCount(element.returnTypeRef.coneTypeOrNull))
						}
						// A function's RETURN / a property's TYPE both hang off the declaration's own range.
						is FirCallableDeclaration -> element.source?.let {
							recordReturn(path, it.startOffset, it.endOffset, ctxCount(element.returnTypeRef.coneTypeOrNull))
						}
						else -> {}
					}
					element.acceptChildren(this)
				}
			}
			f.accept(visitor)
		}
	}

	private fun ctxCount(t: ConeKotlinType?): Int = t?.contextParameterNumberForFunctionType ?: 0
}
