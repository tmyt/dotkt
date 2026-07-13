package kotc.backend

import org.jetbrains.kotlin.descriptors.Visibilities
import org.jetbrains.kotlin.ir.declarations.IrDeclarationWithVisibility
import org.jetbrains.kotlin.ir.declarations.IrFunction
import org.jetbrains.kotlin.ir.declarations.IrParameterKind
import org.jetbrains.kotlin.ir.declarations.IrSimpleFunction
import org.jetbrains.kotlin.ir.symbols.IrClassSymbol
import org.jetbrains.kotlin.ir.types.IrType
import org.jetbrains.kotlin.ir.types.classFqName
import org.jetbrains.kotlin.ir.types.classOrNull
import org.jetbrains.kotlin.ir.types.classifierOrNull
import org.jetbrains.kotlin.ir.types.isMarkedNullable
import org.jetbrains.kotlin.ir.types.isNothing
import org.jetbrains.kotlin.ir.types.isUnit
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import org.jetbrains.kotlin.ir.util.parentClassOrNull

// Design-by-contract on the PUBLIC/PROTECTED surface (#6). JVM Kotlin inserts
// `Intrinsics.checkNotNullParameter(v,"name")` at public-function entry via
// `org.jetbrains.kotlin.backend.jvm.lower.JvmArgumentNullabilityAssertionsLowering` — a JVM-BACKEND lowering that
// is NOT part of the Configuration->FIR->Fir2Ir pipeline kotc runs, so the IR kotc sees carries NO such checks.
// We therefore SYNTHESIZE them as ordinary BIR at each body-emission site, reusing the same null-check/throw shape
// kotc already emits for `x!!` (CHECK_NOT_NULL, BirEmitterCalls.kt): a plain `kotlin.NullPointerException` throw that
// bir2cir's MemberCallSubstitution resolves (@ClrTypeAlias) to the BCL exception. This is a Kotlin-language contract
// (visibility + nullability, both frontend facts); kotc names NO CLR type here.
//
// Postconditions (a deliberate DotKt addition beyond JVM Kotlin, guarding a null leaking OUT via a platform/unsound
// return) live alongside in returnCheckMessage / postconditionReturns, wired at the IrReturn emission (see
// BirEmitterStatements.kt).

/**
 * A NON-NULL REFERENCE type that can meaningfully be null-checked. FALSE for:
 *  - a nullable `T?` (a null is legitimate),
 *  - a type parameter / non-class classifier (generics are REIFIED on the CLR — a bare `T` may instantiate to a
 *    value type, so a null-check would force a box and be semantically meaningless: `clr-all-type-args-reified`),
 *  - a primitive/unsigned (`kotlin.Int`/`kotlin.UInt`/… -> a CLR value type, never null),
 *  - a Kotlin `value`/inline class (-> a CLR value type),
 *  - `Unit`/`Nothing`.
 * These exclusions are Kotlin-language facts (the value-type-ness is read from the IR, not a CLR/BCL FQN table).
 */
internal fun BirEmitter.needsNonNullCheck(t: IrType): Boolean {
	if (t.isMarkedNullable()) return false
	if (t.classifierOrNull !is IrClassSymbol) return false          // type parameter / other -> skip
	if (t.isPrimitiveOrUnsigned()) return false
	if (t.isUnit() || t.isNothing()) return false
	val klass = t.classOrNull?.owner
	if (klass != null && klass.isValue) return false                // value/inline class -> CLR value type
	return true
}

/**
 * Is `fn`'s Kotlin visibility part of the PUBLIC surface a null may cross into from outside the module — PUBLIC,
 * PROTECTED, or `@PublishedApi internal` (the cross-assembly inline surface, which `visOf` also promotes to public)?
 * Gates on the RAW IR visibility, NOT `visOf`'s emitted string: `visOf` maps `Local` to "public" (a trap), so using
 * it would wrongly include local functions. PRIVATE/INTERNAL/LOCAL are trusted within the module -> no check.
 */
internal fun BirEmitter.isPublicSurface(fn: IrDeclarationWithVisibility): Boolean =
	when (fn.visibility.delegate) {
		Visibilities.Public, Visibilities.Protected -> true
		Visibilities.Internal -> {
			val host = (fn as? IrSimpleFunction)?.correspondingPropertySymbol?.owner ?: fn
			host.annotations.any { it.type.classFqName?.asString() == "kotlin.PublishedApi" }
		}
		else -> false
	}

/**
 * A genuine NAMED owner (top-level, or a real class/interface/object) — excludes members of anonymous objects
 * (`object : Iterator<T> {…}`, whose IrClass name is the special `<no name provided>`). Their members are public
 * overrides but synthetic; checking them is only noise/bloat, so skip.
 */
private fun BirEmitter.ownerIsNamed(fn: IrFunction): Boolean {
	val owner = fn.parentClassOrNull ?: return true
	return !owner.name.isSpecial
}

private fun BirEmitter.nullConstJson(): String = """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}"""

/** `if (<ref> == null) throw NullPointerException(<msg>)` as a single `if`-branch BIR statement (no stack merge). */
private fun BirEmitter.nullCheckStmt(refJson: String, msgJson: String): String =
	"""{"k":"if","branches":[{"cond":{"k":"objEq","lhs":$refJson,"rhs":${nullConstJson()}},"body":[{"k":"throw","value":${newExc("kotlin.NullPointerException", msgJson)}}]}]}"""

/**
 * Entry PRECONDITION statements for `fn`'s NON-NULL REFERENCE **value** parameters (a setter's `value` rides as a
 * Regular param). Returns the BIR `if`-throw statements to PREPEND to the body, or empty when `fn` is out of scope
 * (not public surface / anonymous owner / inline). Inline functions are excluded: `method()`'s emitted body IS the
 * `[KotlinInlineBody]` splice payload, so a check would appear only in cross-module splices while same-module splices
 * (spliceBodyWithReturns re-emits the IR independently) would NOT carry it — an inconsistency; and the inline lambda
 * param is the thing JVM explicitly skips anyway.
 *
 * RECEIVERS are NOT checked (only value parameters): the dispatch receiver can never be null (you cannot call a
 * member on null in Kotlin), and an EXTENSION receiver can be LEGITIMATELY null on the CLR — a Kotlin extension on a
 * companion object (`fun String.Companion.format(...)`) lowers to a static method whose `__self` is a null singleton
 * (companion-object elision), so a receiver null-check there fires spuriously (the il-fmt regression). JVM asserts
 * the extension receiver, but that is a JVM-ism the CLR companion representation makes unsound — a deliberate DotKt
 * deviation (docs/dotkt-semantics.md).
 */
internal fun BirEmitter.preconditionChecks(fn: IrFunction): List<String> {
	if (!isPublicSurface(fn) || !ownerIsNamed(fn) || fn.isInline) return emptyList()
	val ownerMethod = fn.fqNameWhenAvailable?.asString() ?: fn.name.asString()
	val out = ArrayList<String>()
	for (p in fn.parameters) {
		if (p.kind != IrParameterKind.Regular) continue             // receivers/context params -> skip (see kdoc)
		if (!needsNonNullCheck(p.type)) continue
		val msg = str("Parameter specified as non-null is null: $ownerMethod, parameter ${p.name.asString()}")
		out.add(nullCheckStmt("""{"k":"local","name":${str(p.name.asString())}}""", msg))
	}
	return out
}

/**
 * The NPE message JSON for `fn`'s NON-NULL REFERENCE return POSTCONDITION, or null when `fn` is out of scope. A
 * postcondition is a deliberate DotKt addition beyond JVM Kotlin — it guards a null leaking OUT of a public member via
 * a platform type / an unsound generic. Registered on `fn`'s return-target symbol around body emission (see
 * `withReturnPostcondition`); `stmt(IrReturn)` then wraps a genuine return value in a bind-check-throw valueBlock.
 * SUSPEND functions are excluded: kotc emits their body plainly and bir2cir builds the Continuation state machine, so
 * wrapping the return value would collide with the shape that lowering rewrites. Inline is excluded for the same
 * splice-inconsistency reason as preconditions.
 */
internal fun BirEmitter.returnCheckMessage(fn: IrSimpleFunction): String? {
	if (!isPublicSurface(fn) || !ownerIsNamed(fn) || fn.isInline || fn.isSuspend) return null
	if (!needsNonNullCheck(fn.returnType)) return null
	val ownerMethod = fn.fqNameWhenAvailable?.asString() ?: fn.name.asString()
	return str("$ownerMethod, non-null return value is null")
}

/** Registers `fn`'s return postcondition (if any) for the duration of `emit`, then removes it. */
internal fun <T> BirEmitter.withReturnPostcondition(fn: IrSimpleFunction, emit: () -> T): T {
	val msg = returnCheckMessage(fn) ?: return emit()
	postconditionReturns[fn.symbol] = msg
	try { return emit() } finally { postconditionReturns.remove(fn.symbol) }
}

/**
 * Wrap a genuine return VALUE in the non-null return POSTCONDITION: bind it to a fresh temp of the (non-null
 * reference) return type, yield it when non-null, else `throw NullPointerException(<msg>)` — the same
 * bind-check-throw shape as a reference `x!!`. `retType` is `fn.returnType`; `msgJson` is the registered message.
 */
internal fun BirEmitter.wrapReturnNonNull(valueJson: String, retType: IrType, msgJson: String): String {
	val nv = "__nn${scopeCounter++}"
	val nvLoc = """{"k":"local","name":${str(nv)}}"""
	val throwNpe = throwExpr(newExc("kotlin.NullPointerException", msgJson))
	return """{"k":"valueBlock","stmts":[{"k":"var","name":${str(nv)},"type":${birType(retType).toJson()},"init":$valueJson}],"result":{"k":"cond","cond":{"k":"unaryOp","op":"!","e":{"k":"objEq","lhs":$nvLoc,"rhs":${nullConstJson()}}},"then":$nvLoc,"else":$throwNpe}}"""
}
