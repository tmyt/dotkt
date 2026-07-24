package kotc.backend

import kotc.bir.TypeNode
import org.jetbrains.kotlin.backend.common.collectTailRecursionCalls
import org.jetbrains.kotlin.descriptors.ClassKind
import org.jetbrains.kotlin.descriptors.Modality
import org.jetbrains.kotlin.descriptors.Visibilities
import org.jetbrains.kotlin.ir.declarations.IrDeclarationWithVisibility
import org.jetbrains.kotlin.ir.declarations.IrAnonymousInitializer
import org.jetbrains.kotlin.ir.declarations.IrClass
import org.jetbrains.kotlin.ir.declarations.IrConstructor
import org.jetbrains.kotlin.ir.declarations.IrFile
import org.jetbrains.kotlin.ir.declarations.IrParameterKind
import org.jetbrains.kotlin.ir.declarations.IrProperty
import org.jetbrains.kotlin.ir.declarations.IrSimpleFunction
import org.jetbrains.kotlin.ir.declarations.IrVariable
import org.jetbrains.kotlin.ir.expressions.IrBlock
import org.jetbrains.kotlin.ir.expressions.IrBlockBody
import org.jetbrains.kotlin.ir.expressions.IrCall
import org.jetbrains.kotlin.ir.expressions.IrConst
import org.jetbrains.kotlin.ir.expressions.IrConstructorCall
import org.jetbrains.kotlin.ir.expressions.IrDelegatingConstructorCall
import org.jetbrains.kotlin.ir.expressions.IrClassReference
import org.jetbrains.kotlin.ir.expressions.IrEnumConstructorCall
import org.jetbrains.kotlin.ir.expressions.IrExpression
import org.jetbrains.kotlin.ir.expressions.IrExpressionBody
import org.jetbrains.kotlin.ir.declarations.IrEnumEntry
import org.jetbrains.kotlin.ir.expressions.IrGetEnumValue
import org.jetbrains.kotlin.ir.expressions.IrGetField
import org.jetbrains.kotlin.ir.expressions.IrGetObjectValue
import org.jetbrains.kotlin.ir.expressions.IrGetValue
import org.jetbrains.kotlin.ir.expressions.IrInstanceInitializerCall
import org.jetbrains.kotlin.ir.expressions.IrReturn
import org.jetbrains.kotlin.ir.expressions.IrSetField
import org.jetbrains.kotlin.ir.expressions.IrSetValue
import org.jetbrains.kotlin.ir.expressions.IrStringConcatenation
import org.jetbrains.kotlin.ir.expressions.IrThrow
import org.jetbrains.kotlin.ir.expressions.IrTry
import org.jetbrains.kotlin.ir.expressions.IrTypeOperatorCall
import org.jetbrains.kotlin.ir.expressions.IrTypeOperator
import org.jetbrains.kotlin.ir.expressions.IrWhen
import org.jetbrains.kotlin.ir.expressions.IrComposite
import org.jetbrains.kotlin.ir.expressions.IrDoWhileLoop
import org.jetbrains.kotlin.ir.expressions.IrVararg
import org.jetbrains.kotlin.ir.expressions.IrSpreadElement
import org.jetbrains.kotlin.ir.expressions.IrFunctionExpression
import org.jetbrains.kotlin.ir.expressions.IrPropertyReference
import org.jetbrains.kotlin.ir.expressions.IrFunctionReference
import org.jetbrains.kotlin.ir.expressions.IrGetClass
import org.jetbrains.kotlin.ir.declarations.IrLocalDelegatedProperty
import org.jetbrains.kotlin.ir.declarations.IrValueDeclaration
import org.jetbrains.kotlin.ir.declarations.IrValueParameter
import org.jetbrains.kotlin.ir.IrElement
import org.jetbrains.kotlin.ir.visitors.IrVisitorVoid
import org.jetbrains.kotlin.ir.visitors.acceptVoid
import org.jetbrains.kotlin.ir.visitors.acceptChildrenVoid
import org.jetbrains.kotlin.ir.types.IrSimpleType
import org.jetbrains.kotlin.ir.types.IrTypeProjection
import org.jetbrains.kotlin.ir.expressions.IrWhileLoop
import org.jetbrains.kotlin.ir.expressions.IrBreak
import org.jetbrains.kotlin.ir.expressions.IrContinue
import org.jetbrains.kotlin.ir.expressions.IrStatementOrigin
import org.jetbrains.kotlin.ir.util.classId
import org.jetbrains.kotlin.ir.types.classOrNull
import org.jetbrains.kotlin.name.CallableId
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import org.jetbrains.kotlin.ir.util.resolveFakeOverride
import org.jetbrains.kotlin.ir.declarations.IrTypeParameter
import org.jetbrains.kotlin.ir.symbols.IrTypeParameterSymbol
import org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI
import org.jetbrains.kotlin.ir.types.IrType
import org.jetbrains.kotlin.ir.types.classFqName
import org.jetbrains.kotlin.ir.types.classifierOrNull
import org.jetbrains.kotlin.ir.types.isUnit
import org.jetbrains.kotlin.ir.types.isMarkedNullable
import org.jetbrains.kotlin.ir.types.isBoxedArray
import org.jetbrains.kotlin.ir.types.isPrimitiveType
import org.jetbrains.kotlin.ir.types.isUnsignedType
import org.jetbrains.kotlin.ir.util.isPrimitiveArray
import org.jetbrains.kotlin.ir.util.isUnsignedArray
import org.jetbrains.kotlin.ir.util.defaultType
import org.jetbrains.kotlin.ir.types.makeNotNull
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import org.jetbrains.kotlin.ir.IrFileEntry
import org.jetbrains.kotlin.cli.common.messages.MessageCollector
import org.jetbrains.kotlin.cli.common.messages.CompilerMessageSeverity
import org.jetbrains.kotlin.cli.common.messages.CompilerMessageLocation
import java.io.File

/** The BIR placeholder for an OMITTED default argument this build cannot inline (a cross-module default whose VALUE
 *  the frontend KLIB dropped → IrErrorExpression). Emitted POSITIONALLY so a later provided arg keeps its slot;
 *  bir2cir's DefaultArgSplice replaces it (by array index) from the callee's ref.dll @KotlinDefault / [DefaultParameterValue]. */
private val defaultArgPlaceholder = """{"k":"defaultArg"}"""

private val defaultArgThisToken = """{"k":"this"}"""

/** Regular args, POSITIONALLY complete, filling omitted default arguments (IL has no default-parameter mechanism).
 *  Fill source by default KIND: a same-module CONSTANT/global default is inlined verbatim; a same-module default that
 *  reads the callee's RECEIVER (`missingDelimiterValue = this`, a data-class `copy`'s `y = this.y`) is inlined with
 *  `this` rewritten to THIS call's receiver (the JVM `$default` scope, done at the JSON level); a CROSS-MODULE default
 *  (IrErrorExpression — the jar preserves no default VALUE) becomes a `defaultArg` placeholder that bir2cir fills from
 *  the ref.dll. The placeholder is emitted whenever the callee carries @KotlinDefault OR a LATER arg is provided (a
 *  "gap" — silently omitting it would shift the later arg into the wrong parameter slot: the joinToString/substringAfter
 *  miscompile); a purely TRAILING cross-module omit on a metadata-representable callee is still dropped so ilemit's
 *  [DefaultParameterValue] backfill fills it (unchanged). */
internal fun BirEmitter.filledArgs(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): List<String> {
	val callee = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction) ?: return emptyList()
	val carries = (callee as? org.jetbrains.kotlin.ir.declarations.IrSimpleFunction)?.let { carriesKotlinDefault(it) } ?: false
	val receiverSyms = callee.parameters.filter {
		it.kind == IrParameterKind.DispatchReceiver || it.kind == IrParameterKind.ExtensionReceiver
	}.map { it.symbol }.toHashSet()
	val valueSyms = callee.parameters.filter { it.kind == IrParameterKind.Regular }.map { it.symbol }.toHashSet()
	// The call's receiver expression (for `this`-referencing same-module defaults): the extension receiver if any, else
	// the dispatch receiver (a data-class `copy` is a member, so its `this.y` default resolves to the dispatch receiver).
	// Emitted lazily and reused per omitted default — single-eval is best-effort (a trivial local/this receiver is safe
	// to duplicate; a side-effecting receiver read by several omitted defaults is a documented edge).
	val recvJson: String? by lazy { (extensionReceiver(call) ?: dispatchReceiver(call))?.let { expr(it) } }
	val regs = callee.parameters.mapIndexedNotNull { i, p -> if (p.kind == IrParameterKind.Regular) i to p else null }
	val provided = regs.map { (i, _) -> if (i < call.arguments.size) call.arguments[i] else null }
	val out = ArrayList<String>()
	// The filled JSON for each already-processed value parameter — the substitution source for a same-module default
	// that reads ANOTHER value parameter (`b: Int = a * 10`). A Kotlin default may reference only EARLIER params, so
	// every referenced param is already recorded here by the time its reader is processed.
	val filledByParam = java.util.IdentityHashMap<org.jetbrains.kotlin.ir.declarations.IrValueParameter, String>()
	regs.forEachIndexed { idx, pair ->
		val p = pair.second
		val arg = provided[idx]
		val emitted: String? = when {
			arg != null -> argExpr(arg, p)
			else -> {
				val def = p.defaultValue?.expression
				when {
					def == null -> null
					def is org.jetbrains.kotlin.ir.expressions.IrErrorExpression ->
						// CROSS-MODULE: the jar dropped the default VALUE. A data-class `copy` (Pair/Triple, or any referenced
						// data class) is a SPECIAL case: its omitted-field default is ALWAYS `this.<field>` by construction, so
						// reconstruct it as a receiver FIELD read at the INSTANTIATED call site — the exact BIR kotc emits for a
						// plain `pair.first` (owner = the actual `kotlin.Pair[Int,Int]`, so no generic `gp:` token leaks; the
						// @KotlinDefault splice can't carry that instantiation). This is the Pair/Triple partial-`copy` fix (C3).
						if ((callee as? org.jetbrains.kotlin.ir.declarations.IrSimpleFunction)?.let { isDataClassCopy(it) } == true && recvJson != null)
							(dispatchReceiver(call) ?: extensionReceiver(call))?.let { r ->
								// Owner via ownerSpec (the SAME token the plain `pair.first` property read uses — the referenced,
								// instantiated `kotlin.Pair[Int,Int]`, no `@` this-assembly prefix, no open `gp:` param).
								"""{"sty":${birType(p.type).toJson()},"k":"field","ownerType":${ownerSpec(callee.parent as? IrClass, r.type).toJson()},"recv":${recvJson},"name":${str(p.name.asString())}}"""
							}
						// A @KotlinDefault-carrying callee (any non-constant default — joinToString's CharSequence separators,
						// substringAfter's `= this`, `b = a * 10`) gets a POSITIONAL placeholder for EVERY omitted arg so a later
						// provided arg (the trailing transform lambda) keeps its slot; bir2cir fills each from the ref.dll
						// @KotlinDefault (its `{param n}` tokens → this call's args). A callee with only metadata-representable
						// defaults carries none → drop the (trailing) omit for ilemit's [DefaultParameterValue] backfill.
						else if (carries) defaultArgPlaceholder else null
					refsAny(def, valueSyms) -> {
						// SAME-MODULE default reading another VALUE parameter (`b: Int = a * 10`). Inline with each referenced
						// value param rewritten to THIS call's filled arg for that param — the $default-scope evaluation at the
						// emitted-JSON level (the twin of the `= this` receiver case below, via captureSubst instead of a
						// token replace). Best-effort single-eval: a side-effecting earlier arg read by this default is
						// duplicated (documented edge, same as the receiver case).
						val installed = ArrayList<org.jetbrains.kotlin.ir.declarations.IrValueParameter>()
						for ((vp, js) in filledByParam) { captureSubst[vp] = js; installed.add(vp) }
						val js = recvJson?.let { expr(def).replace(defaultArgThisToken, it) } ?: expr(def)
						installed.forEach { captureSubst.remove(it) }
						js
					}
					refsAny(def, receiverSyms) -> {
						// SAME-MODULE default reading the RECEIVER (`= this` / `this.field`). Inline with `this` rewritten to
						// THIS call's receiver — the $default-scope evaluation, at the emitted-JSON level. Every `this` in the
						// callee's default denotes the callee's receiver, so replacing them ALL with this call's receiver is
						// correct (an inserted `{"k":"this"}` from a `this.foo` receiver then denotes the CALLER's this).
						val r = recvJson
						if (r != null) expr(def).replace(defaultArgThisToken, r) else argExpr(def, p)
					}
					else -> argExpr(def, p)   // constant / global — inline verbatim (unchanged)
				}
			}
		}
		if (emitted != null) { out.add(emitted); filledByParam[p] = emitted }
	}
	return out
}

/** The call's regular args IN ORDER, filling an omitted default-arg param with its callee's default-value
 *  expression. A restored function/ctor carries a real constant default (applyDefaults), so the consumer can omit a
 *  default arg ANYWHERE — trailing, named-middle (`f(c=9)`), or reordered — and the value is filled here. */
internal fun BirEmitter.filledArgExprs(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): List<IrExpression> {
	val callee = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction) ?: return emptyList()
	val calleeLocals = callee.parameters.map { it.symbol }.toHashSet()
	// #134: the facadegen metadata's constant defaults for this callee's regular params, keyed by resolved IR identity —
	// a CROSS-MODULE facadegen constructor/top-level function's injected default deserializes as an IrErrorExpression
	// (fir2ir drops the VALUE for a bodies-skipped dependency declaration), so the real value is read from here to fill an
	// omitted default arg ANYWHERE (named-middle `f(c=9)` / reordered — a trailing omit falls back to ilemit's
	// [DefaultParameterValue] backfill either way). Null for a same-module callee (its default is a real IrConst in IR).
	val regCount = callee.parameters.count { it.kind == IrParameterKind.Regular }
	val metaDefaults = injectedMetaDefaults(callee, regCount)
	val out = ArrayList<IrExpression>()
	var regIdx = -1
	callee.parameters.forEachIndexed { i, p ->
		if (p.kind != IrParameterKind.Regular) return@forEachIndexed
		regIdx++
		val arg = if (i < call.arguments.size) call.arguments[i] else null
		if (arg != null) out.add(arg)
		else (p.defaultValue?.expression)?.let { def ->
			// A CROSS-MODULE callee's default value does NOT deserialize from the jar/metadata as a real IR expression:
			// the frontend hands back an IrErrorExpression placeholder. #134: fill it from the facadegen metadata's REAL
			// constant default (synthesized as an IrConst) so a named-middle / reordered omission is correct. If the
			// metadata has no value for this slot, OMIT the (trailing) arg — ilemit's call path then fills it from the
			// callee's .NET [DefaultParameterValue] metadata (EmitCallArgs), the intended "constant default -> metadata
			// -> ilemit fill" path (e.g. Regex.find's startIndex=0).
			if (def is org.jetbrains.kotlin.ir.expressions.IrErrorExpression) {
				val filled = metaDefaults?.getOrNull(regIdx)?.let { metaConstArg(it, p.type) }
				if (filled != null) { out.add(filled); return@let }
				// The metadata carries no constant for this omitted slot. A purely TRAILING omit is safe (ilemit's
				// [DefaultParameterValue] backfill fills it). But if a LATER arg is provided, silently omitting THIS slot
				// would slide that arg into the wrong parameter (the joinToString/substringAfter miscompile class) — refuse
				// loudly instead of miscompiling.
				val laterArgProvided = (i + 1 until call.arguments.size).any { j ->
					call.arguments[j] != null && callee.parameters[j].kind == IrParameterKind.Regular
				}
				if (laterArgProvided) unsupported(call, "omitting a cross-module default argument the metadata does not carry",
					"the default value of parameter '${p.name.asString()}' is not available as a constant in the referenced " +
					"assembly's metadata; pass the argument explicitly")
				return@let
			}
			// Filling an OMITTED default inlines the callee's default expression at THIS call site — fine for a
			// constant/global, but a default that reads the callee's OWN parameters/receiver (`b: Int = a * 10`, or a
			// data class `copy`'s `x = this.x`) must be evaluated in the callee's scope (cf. Kotlin/JVM's `$default`),
			// which the .NET backend doesn't yet do. Reject only HERE — at the omitting call — not at the declaration:
			// a data class whose `copy` is never arg-omitted must still compile. Otherwise a dangling `local a`/`this`
			// reaches ilemit as invalid IL. See docs/dotkt-semantics.md (non-constant default arguments).
			if (refsAny(def, calleeLocals)) unsupported(call, "omitting a non-constant default argument",
				"the default value of parameter '${p.name.asString()}' references other parameters or the receiver, " +
				"which the .NET backend cannot evaluate at the call site; pass the argument explicitly")
			out.add(def)
		}
	}
	return out
}

/** The facadegen metadata's per-regular-parameter constant defaults for `callee` (ctor by IR ClassId, or top-level
 *  function by resolved CallableId), matched by regular-param count. Null for a same-module callee or one with no such
 *  overload / ambiguous defaults. Shared by [filledArgExprs] (#134 const inline) and [filledInjectedArgs] (#146). */
internal fun BirEmitter.injectedMetaDefaults(callee: org.jetbrains.kotlin.ir.declarations.IrFunction, regCount: Int): List<kotc.frontend.ClrConstDefault?>? =
	when (callee) {
		is org.jetbrains.kotlin.ir.declarations.IrConstructor ->
			(callee.parent as? IrClass)?.classId?.let { kotc.frontend.clrInjectedCtorParamDefaults(it, regCount) }
		is org.jetbrains.kotlin.ir.declarations.IrSimpleFunction ->
			(callee.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)
				?.let { kotc.frontend.clrInjectedTopLevelParamDefaults(org.jetbrains.kotlin.name.CallableId(it.packageFqName, callee.name), regCount) }
		else -> null
	}

/** The non-constant Kotlin-default carrier slots restored by facadegen for an injected constructor/top-level function.
 * Unlike [carriesKotlinDefault], this survives the bodies-skipped dependency IR, where declaration-origin/default-body
 * details are intentionally incomplete. */
internal fun injectedKotlinDefaultSlots(callee: org.jetbrains.kotlin.ir.declarations.IrFunction, regCount: Int): List<Boolean>? =
	when (callee) {
		is org.jetbrains.kotlin.ir.declarations.IrConstructor ->
			(callee.parent as? IrClass)?.classId?.let { kotc.frontend.clrInjectedCtorKotlinDefaultSlots(it, regCount) }
		is org.jetbrains.kotlin.ir.declarations.IrSimpleFunction ->
			(callee.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)
				?.let { kotc.frontend.clrInjectedTopLevelKotlinDefaultSlots(org.jetbrains.kotlin.name.CallableId(it.packageFqName, callee.name), regCount) }
		else -> null
	}

/** #146: the regular-arg BIR STRINGS for a call to a facadegen-injected TOP-LEVEL function, filling an OMITTED default:
 *  a metadata-representable CONSTANT is synthesized inline (#134, [metaConstArg]); a NON-CONSTANT default (`= {}`, a
 *  call, any expr the metadata can't carry) becomes a POSITIONAL `defaultArg` placeholder — bir2cir's DefaultArgSplice
 *  fills it from the callee's ref.dll `[kotlin.clr.KotlinDefault]` BIR sub-tree. Positional so a later provided arg keeps
 *  its slot (`col2(build = {...})` omitting a leading `configure`). A slot with neither a constant NOR a @KotlinDefault
 *  is dropped when purely trailing (ilemit's [DefaultParameterValue] backfill), else refused loudly (arg-shift guard).
 *  The extension receiver is prepended by the caller; the @KotlinDefault index counts it first, matching the final args
 *  array's positions. */
internal fun BirEmitter.filledInjectedArgs(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): List<String> {
	val callee = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction) ?: return emptyList()
	val regCount = callee.parameters.count { it.kind == IrParameterKind.Regular }
	val metaDefaults = injectedMetaDefaults(callee, regCount)
	val kotlinDefaultSlots = injectedKotlinDefaultSlots(callee, regCount)
	val carries = (callee as? org.jetbrains.kotlin.ir.declarations.IrSimpleFunction)?.let { carriesKotlinDefault(it) } ?: false
	val out = ArrayList<String>()
	var regIdx = -1
	callee.parameters.forEachIndexed { i, p ->
		if (p.kind != IrParameterKind.Regular) return@forEachIndexed
		regIdx++
		val arg = if (i < call.arguments.size) call.arguments[i] else null
		if (arg != null) { out.add(expr(arg)); return@forEachIndexed }
		val def = p.defaultValue?.expression ?: return@forEachIndexed
		if (def is org.jetbrains.kotlin.ir.expressions.IrErrorExpression) {
			metaDefaults?.getOrNull(regIdx)?.let { metaConstArg(it, p.type) }?.let { out.add(expr(it)); return@forEachIndexed }
			if (kotlinDefaultSlots?.getOrNull(regIdx) == true || carries) {
				out.add(defaultArgPlaceholder)
				return@forEachIndexed
			}
			val laterArgProvided = (i + 1 until call.arguments.size).any { j ->
				call.arguments[j] != null && callee.parameters[j].kind == IrParameterKind.Regular
			}
			if (laterArgProvided) unsupported(call, "omitting a cross-module default argument the metadata does not carry",
				"the default value of parameter '${p.name.asString()}' is not available in the referenced assembly's " +
				"metadata (no constant and no @KotlinDefault); pass the argument explicitly")
			return@forEachIndexed
		}
		out.add(expr(def))
	}
	return out
}

/** #134: synthesize an IrConst for a facadegen-metadata constant default (`ClrConstDefault`), typed with the omitted
 *  parameter's IR type, so `expr()` renders the ordinary `{"k":"const",…}` node a same-module default would produce.
 *  Mirrors ClrTypeInjection.optDefault's value parse (the SAME metadata the FIR injection reads). A null value or an
 *  unparseable/unhandled kind -> null (the arg is omitted; ilemit's [DefaultParameterValue] backfill covers a trailing
 *  slot). */
private fun metaConstArg(d: kotc.frontend.ClrConstDefault, type: IrType): IrExpression? {
	val so = org.jetbrains.kotlin.ir.util.SYNTHETIC_OFFSET
	val C = org.jetbrains.kotlin.ir.expressions.impl.IrConstImpl
	// A null default value (`= null`) -> the null literal, mirroring ClrTypeInjection.optDefault (which builds it
	// UNCONDITIONALLY). A null default only sits on a reference/nullable param — the param's own IR type carries the
	// (possibly flexible/oblivious `T!`) nullability, so keep it as the const's type rather than gating on isMarkedNullable
	// (an oblivious `String!` reads non-null but still legitimately accepts a null default the frontend already admitted).
	val v = d.value ?: return C.constNull(so, so, type)
	return when (d.valueType) {
		"Int" -> C.int(so, so, type, v.toIntOrNull() ?: return null)
		"Long" -> C.long(so, so, type, v.toLongOrNull() ?: return null)
		"Short" -> C.short(so, so, type, v.toShortOrNull() ?: return null)
		"Byte" -> C.byte(so, so, type, v.toByteOrNull() ?: return null)
		"Boolean" -> C.boolean(so, so, type, v == "true")
		"Double" -> C.double(so, so, type, v.toDoubleOrNull() ?: return null)
		"Float" -> C.float(so, so, type, v.toFloatOrNull() ?: return null)
		"Char" -> C.char(so, so, type, v.firstOrNull() ?: return null)
		"String" -> C.string(so, so, type, v)
		else -> null
	}
}

/** True if `expr` reads any of `locals` — detects a default-arg expression that references the callee's own
 *  parameters/receiver (e.g. `b = a * 10`, or a data class `copy`'s `this.x`), which can't be inlined at a call site. */
internal fun BirEmitter.refsAny(expr: IrExpression, locals: Set<org.jetbrains.kotlin.ir.symbols.IrValueSymbol>): Boolean {
	var found = false
	expr.acceptVoid(object : IrVisitorVoid() {
		override fun visitElement(element: IrElement) {
			if (found) return
			if (element is IrGetValue && element.symbol in locals) { found = true; return }
			element.acceptChildrenVoid(this)
		}
	})
	return found
}

internal fun BirEmitter.regularArgs(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): List<IrExpression> {
	val params = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.parameters ?: emptyList()
	return call.arguments.mapIndexedNotNull { i, a ->
		if (a != null && i < params.size && isValueParameter(params[i])) a else null
	}
}

internal fun BirEmitter.dispatchReceiver(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): IrExpression? {
	val params = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.parameters ?: return null
	val idx = params.indexOfFirst { it.kind == IrParameterKind.DispatchReceiver }
	return if (idx in 0 until call.arguments.size) call.arguments[idx] else null
}

/** The callee's ordinary (non-receiver) value parameters, in order. */
internal fun BirEmitter.regularParams(callee: org.jetbrains.kotlin.ir.declarations.IrFunction): List<IrValueParameter> =
	callee.parameters.filter { isValueParameter(it) }

internal fun BirEmitter.extensionReceiver(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): IrExpression? {
	val params = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.parameters ?: return null
	val idx = params.indexOfFirst { it.kind == IrParameterKind.ExtensionReceiver }
	return if (idx in 0 until call.arguments.size) call.arguments[idx] else null
}

/** #144: the extension-receiver classifier-ClassId key of a top-level extension callee, for disambiguating a
 *  facadegen-injected `CallableId(package,name)` that two same-name/same-arity extensions on DIFFERENT receiver types
 *  share (they live in distinct .NET static classes / file classes). Read straight off the RESOLVED callee's declared
 *  extension-receiver `IrType` classifier `classId` — the SAME ClassId the injector's `coneOf` produced from the
 *  metadata, so it string-matches `TopLevelSig.receiverKey` across facadegen's name vocabulary (a raw type-name compare
 *  would diverge for `String`/primitive/generic/array receivers). Null for a non-extension callee, or a type-variable /
 *  function-type receiver (no class classifier) — the arity-only path stays byte-identical. See `clrInjectedTopLevelFileClass`. */
internal fun BirEmitter.injectedExtReceiverKey(fn: org.jetbrains.kotlin.ir.declarations.IrFunction): String? =
	fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
		?.type?.classOrNull?.owner?.classId?.asString()

/** Same index-by-parameter-kind approach as [dispatchReceiver]/[extensionReceiver], for an `IrPropertyReference`
 *  (which has no callee `IrFunction` of its own — the getter's parameter SHAPE is used to index its `arguments`).
 *  `IrMemberAccessExpression.dispatchReceiver`/`.extensionReceiver` (the convenience getters) are ERROR-level
 *  deprecated in this compiler version, so kotc never calls them directly, here or on an `IrCall`. */
internal fun BirEmitter.propRefDispatchReceiver(node: IrPropertyReference): IrExpression? {
	val params = (node.getter?.owner ?: node.setter?.owner)?.parameters ?: return null
	val idx = params.indexOfFirst { it.kind == IrParameterKind.DispatchReceiver }
	return if (idx in 0 until node.arguments.size) node.arguments[idx] else null
}

/** Whether an instance call dispatches VIRTUALLY (callvirt) or is a plain non-virtual `call`.
 *  A `super.X()` call (IrCall.superQualifierSymbol != null) MUST be non-virtual: the callee already points at the
 *  RESOLVED super-class slot, so a `callvirt` would re-dispatch by the receiver's runtime type back to the OVERRIDE
 *  and infinite-loop (issue #14). Otherwise virtual iff the callee is open/overriding. */
internal fun isVirtualInstanceCall(call: IrCall, callee: IrSimpleFunction): Boolean =
	call.superQualifierSymbol == null && (callee.modality != Modality.FINAL || callee.overriddenSymbols.isNotEmpty())

internal fun BirEmitter.call(call: IrCall): String {
	// A `tailrec` self-tail-call -> a back-jump to the method entry (TCO, §2b) instead of a recursive call. Matched
	// by IR identity against the frontend-validated tail-call set installed by `method()`.
	tailrecCtx?.let { ctx -> if (call in ctx.calls) return tailrecJump(call, ctx) }
	val callee = call.symbol.owner
	// NOTE: kotlin.text.MatchResult.value is a REAL interface property (realized by ClrMatchResult) — it must route
	// through the ordinary member-call path, NOT a hardcoded System...Match.Value lowering (that leftover forced the
	// broken MatchResult->Match aliasing above and mis-typed the call).
	// `.message`/`.cause` on a Throwable subclass is a PLAIN Kotlin property read: kotc emits the ordinary
	// `callInstance get_message`/`get_cause` (with its `overrides` chain to kotlin.Throwable) below, and bir2cir
	// substitutes it to `clrPropGet System.Exception.Message`/`.InnerException` off the @ClrProperty binding on the
	// ref.dll (kotlin.Throwable is @ClrTypeAlias("System.Exception")). No BCL member name lives in kotc (layer purity).
	// `kotlin.sequences.sequence { yield(…) }` is now ORDINARY library code: it resolves to the real stdlib
	// `sequence(block)` function over the cold core (SequenceBuilderIterator), with `{ yield(...) }` flowing through
	// the ordinary suspend-lambda path (newSuspendLambda -> bir2cir's RestrictedSuspendLambda SM). kotc has NO
	// knowledge of the `sequence`/`yield`/`yieldAll` symbols — the compiler no longer knows the builder exists.
	// `stackBuffer(n) { … }` intrinsic -> scoped stack allocation (splice the block into the caller's frame).
	// Matched by FULL name (`kotlin.clr.stackBuffer`, its CLR-intrinsic home) so a user function happening to be
	// named `stackBuffer` is not mistaken for the intrinsic.
	if (callee.fqNameWhenAvailable?.asString() == "kotlin.clr.stackBuffer")
		return emitStackBuffer(call)
	// A .NET event subscription `w.Changed.subscribe(h)` resolves (normal Kotlin resolution) to a member of the
	// injected `kotlin.clr.ClrEvent<T>` fiction (the surfaced form of a .NET event member — see ClrTypeInjection).
	// kotc emits the PLAIN Kotlin call identity: a
	// `callInstance` on `kotlin.clr.ClrEvent` whose receiver is the event member-access `w.Changed` (a clrEventGet
	// carrying the .NET owner type + event name). NO `add_`/`remove_` naming, NO clrEventAdd here — bir2cir's
	// ClrEventSubscriptionBinding recognizes this node and binds it to the .NET add/remove accessors (the Kotlin<->CLR
	// event relation lives in bir2cir, not kotc). The ClrEvent<T> value is never materialized.
	if (callee.name.asString() == "subscribe"
		&& (callee.parent as? IrClass)?.fqNameWhenAvailable?.asString() == "kotlin.clr.ClrEvent") {
		val recv = dispatchReceiver(call)!!
		// The receiver here is the ONLY legitimate ClrEvent-value position (the event member-access `w.Changed`);
		// emit it with the OK flag so its clrPropGet is allowed. Every other ClrEvent read stays a compile error.
		val recvJson = asClrEventReceiver { expr(recv) }
		return """{"k":"callInstance","ownerType":${birType(recv.type).toJson()},"virtual":false,"recv":$recvJson,"method":${str(callee.name.asString())},"args":[${expr(regularArgs(call).first())}]}"""
	}
	// RAISE: `handle.invoke(sender, args)` / `handle(sender, args)` (both desugar to `ClrEvent.invoke`). The event handle
	// is a member read `vm.<E>` (a `ClrEvent<T>` property); raise is legal only for a KOTLIN-DECLARED event (one with a
	// synthesized `raise_<E>`). kotc lowers this to a dedicated dialect node `clrEventRaise` carrying the RECEIVER's static
	// type (the type that declares `raise_<E>`) + the event name + the invoke args — bir2cir's ClrEventImplBinding binds it
	// to a `raise_<E>` call (and hard-errors a raise on a CONSUMED foreign event). The ClrEvent<T> value is consumed, never
	// materialized — we emit the underlying receiver `vm`, not the handle read.
	if (callee.name.asString() == "invoke"
		&& (callee.parent as? IrClass)?.fqNameWhenAvailable?.asString() == "kotlin.clr.ClrEvent") {
		val handle = dispatchReceiver(call)!!
		val eventAccess = handle as? IrCall
		val prop = eventAccess?.symbol?.owner?.correspondingPropertySymbol?.owner
		val eventRecv = eventAccess?.dispatchReceiver
		if (prop == null || eventRecv == null) {
			hadError = true
			messageCollector?.report(CompilerMessageSeverity.ERROR,
				"a .NET event can be raised only through an instance event handle (`vm.<Event>.invoke(...)`)", locationOf(call))
			return """{"k":"unsupportedExpr","of":"clr-event-raise-non-instance-handle"}"""
		}
		// invoke is `vararg args: Any?` — the individual sender/args arrive wrapped in a single IrVararg; unwrap them.
		val rawArgs = regularArgs(call)
		val argExprs = if (rawArgs.size == 1 && rawArgs[0] is IrVararg)
			(rawArgs[0] as IrVararg).elements.filterIsInstance<IrExpression>() else rawArgs
		return """{"k":"clrEventRaise","type":${birType(eventRecv.type).toJson()},"event":${str(prop.name.asString())},"recv":${expr(eventRecv)},"args":[${argExprs.joinToString(",") { expr(it) }}]}"""
	}
	// A `StackBuffer<T>` member access while its block is being spliced -> a stack op (ptr + index).
	((dispatchReceiver(call) as? IrGetValue)?.symbol?.owner)?.let { stackBufSubst[it] }?.let { return emitStackBufferOp(call, callee, it) }
	// A `<get-x>`/`<set-x>` call for a LOCAL delegated property -> access on the delegate local (thisRef=null,
	// no enclosing instance). `by lazy`: the local's `.Value`; custom delegate: getValue/setValue(null, KProperty).
	localDelegates[callee]?.let { ldp ->
		val dvar = ldp.delegate!!
		val dlocal = """{"k":"local","name":${str(dvar.name.asString())}}"""
		val elem = birType(ldp.getter.returnType)
		// A `ClrRef<T>` delegate (byref local): getValue/setValue inline to ldobj/stobj through the managed pointer.
		if (birType(dvar.type) is TypeNode.ByRef)
			return if (callee === ldp.setter)
				"""{"k":"byrefStore","local":${str(dvar.name.asString())},"elem":${str(elem)},"value":${expr(regularArgs(call).first())}}"""
			else """{"k":"byrefLoad","local":${str(dvar.name.asString())},"elem":${str(elem)}}"""
		// `by lazy` (local): the delegate is a real `kotlin.Lazy<T>` (the stdlib `UnsafeLazyImpl`). Its accessor is
		// the InlineOnly `Lazy<T>.getValue(…) = value` operator, whose stdlib inline body is absent from our IR;
		// inline it (a pure Kotlin-frontend fact) to a plain read of the Lazy interface's `value` getter. bir2cir/
		// ilemit resolve the real emitted `kotlin.Lazy::get_value` — no CLR (System.Lazy) knowledge in kotc.
		if (dvar.type.classFqName?.asString() == "kotlin.Lazy" && callee === ldp.getter) {
			val owner = ownerSpec(dvar.type.classifierOrNull?.owner as? IrClass, dvar.type)
			return """{"k":"callInstance","ownerType":${owner.toJson()},"virtual":true,"recv":$dlocal,"method":"get_value","args":[]${retHint((owner as? TypeNode.Fqn)?.args != null, ldp.getter.returnType)}}"""
		}
		val delegateClass = dvar.type.classifierOrNull?.owner as? IrClass
		val dvFq = dvar.type.classFqName?.asString()
		// A user delegate class -> its concrete type; a stdlib Read(Write)Property-typed delegate (e.g.
		// `by Delegates.observable(…)`) -> the REAL generic stdlib interface (mirrors `by lazy` on real
		// `kotlin.Lazy<T>`), binding to the actual emitted stdlib getValue/setValue.
		val (owner, ownerGeneric) = when {
			delegateClass != null && !isExternalNetType(delegateClass) &&
				delegateClass.fqNameWhenAvailable?.asString()?.startsWith("kotlin") != true -> fqnJson(typeName(delegateClass)) to false
			dvFq == "kotlin.properties.ReadWriteProperty" || dvFq == "kotlin.properties.ReadOnlyProperty" -> {
				val os = ownerSpec(delegateClass, dvar.type)
				os.toJson() to ((os as? TypeNode.Fqn)?.args != null)
			}
			else -> null to false
		}
		if (owner != null) {
			val kprop = kPropertyStub(ldp.name.asString())
			val nullRef = """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}"""
			return if (callee === ldp.setter)
				"""{"k":"callInstance","ownerType":$owner,"virtual":true,"recv":$dlocal,"method":"setValue","args":[$nullRef,$kprop,${expr(regularArgs(call).first())}]}"""
			else """{"k":"callInstance","ownerType":$owner,"virtual":true,"recv":$dlocal,"method":"getValue","args":[$nullRef,$kprop]${retHint(ownerGeneric, ldp.getter.returnType)}}"""
		}
	}
	val name = callee.name.asString()
	val declaringClass = callee.parent as? IrClass
	// A top-level fn has no declaringClass; fall back to the callee's OWN package so an injected/user top-level
	// operator (e.g. a restored `operator fun Vec.plus`) isn't mistaken for a kotlin builtin and lowered to a `bin`.
	val isBuiltin = (declaringClass?.fqNameWhenAvailable?.asString() ?: callee.fqNameWhenAvailable?.asString())?.startsWith("kotlin") ?: true
	val pkgFqName = (callee.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)?.packageFqName?.asString()
	val calleeFq = if (declaringClass == null && pkgFqName != null) "$pkgFqName.$name" else null
	
	// A top-level fun annotated @ClrIntrinsic is NOT bound to a STATIC/INSTANCE .NET call here: that
	// @ClrIntrinsic-driven member-call SUBSTITUTION belongs to bir2cir (sourced from the ref.dll), NOT kotc.
	// kotc emits the PLAIN Kotlin top-level call (the clrStatic file-class path below for injected .NET top-level
	// funs is metadata-driven and stays). See [clrName] / CLAUDE.md "kotc reads
	// NEITHER @ClrIntrinsic NOR @ClrTypeAlias".

	// NOTE: collection-interface member routing — `iterator()`/`isEmpty`/`contains`/`containsAll`/`indexOf`/
	// `lastIndexOf`/`subList`/`listIterator()` on a @ClrTypeAlias `kotlin.collections` interface, whose substituted
	// BCL IReadOnly*/IEnumerable face lacks these slots — is OWNED BY bir2cir Rule 5 (Program.cs ~4979), which routes
	// them to the rt `ClrIteratorBridge`/`ClrCollectionDefaults` helpers off the ref.dll @ClrTypeAlias metadata. kotc
	// emits the PLAIN member call (faithful IR); it does NOT name the helper class.

	// A call to a lifted local function -> static call with captured values (incl. enclosing `this`) prepended.
	localFns[callee]?.let { (lname, caps, tps) ->
		val capArgs = caps.map { capValueExpr(it) }
		// The lift emits the callee's OWN value params in declaration order (receivers before regulars, see liftLocalFn),
		// so a receiver-bearing local (a local extension fun called as `x.f()`) must pass its dispatch/extension receiver
		// value in that SAME slot, between the captures and the regular args. (A plain local fn has no receiver params →
		// empty → byte-identical to before.)
		val recvArgs = callee.parameters.filter {
			it.kind == IrParameterKind.DispatchReceiver || it.kind == IrParameterKind.ExtensionReceiver
		}.mapNotNull { p ->
			(if (p.kind == IrParameterKind.DispatchReceiver) dispatchReceiver(call) else extensionReceiver(call))?.let { expr(it) }
		}
		// If the lifted method is generic (captured enclosing type params), pass them as type arguments.
		val typeArgs = if (tps.isEmpty()) "" else ""","typeArgs":[${tps.joinToString(",") { tvOf(it).toJson() }}]"""
		// #199 DESIGN B — same two-axis contract as a top-level function call, completing it for the LAST `owner:null`
		// callStatic shape. The lifted static `__local<n>_<fn>` lives in the CURRENT file's file class (liftLocalFn ->
		// liftedMethods). `owner:null` stays (the load-bearing substitution/recognition axis — a lifted-local call is
		// never intrinsic-substituted, but the invariant that owner:null ⇔ "not owned by a named class" holds), and the
		// DISPATCH hint `calleeOwner = <this file class>` scopes ilemit's lookup to the RIGHT file class FIRST (mirrors
		// `sty`; the owner-null substitution machinery IGNORES it). Uses `fileClass` directly, not `calleeOwnerTag`,
		// whose IrFile-parent gate excludes a local fn (its parent is the enclosing function).
		//   The name `__local<n>` embeds `scopeCounter`, which is MONOTONIC across all files in one kotc invocation (one
		// BirEmitter reused per file; emitFile resets liftedMethods but NOT scopeCounter). Every canonical build is one
		// invocation per assembly, so two `__local<n>` with the same `<n>` never coexist in an assembly and ilemit's
		// the old global lookup happened to resolve correctly — explicit ownership is still mandatory (no reproducible mis-dispatch
		// in a single-invocation build; only two SEPARATE kotc invocations linked into one assembly collide on `__local0`).
		// It is the method-dispatch analog of `synthScope` (which already per-file-prefixes synthetic closure TYPE names
		// against the same cross-file link collision) and closes the Design-B rule that every owner:null callStatic
		// carries its FIR-resolved dispatch owner.
		return """{"k":"callStatic","owner":null,"method":${str(lname)},"args":[${(capArgs + recvArgs + filledArgs(call)).joinToString(",")}]$typeArgs,"calleeOwner":${fqnJson(fileClass)}}"""
	}

	// Inlining (lambda-param inline funs only; lambda-less inline = JIT's job — see [[clr-not-jvm-discard-jvmisms]]).
	// An `action(x)` invoke on a lambda param is NO longer special-cased: mechanism-1 is retired (#75 S4b), so an
	// inline fun is a REAL emitted generic method taking a delegate, and `action(x)` inside its body is just the
	// ordinary `callInstance` invoke on that delegate (the fall-through member-call path). bir2cir owns any splice.
	// `(::x)()` (invoking a property-reference VALUE inline) needs NO special handling here: KProperty0/
	// KProperty1's declared `() -> V`/`(T) -> V` supertype gives them a REAL fake-overridden `invoke` abstract
	// member declared directly ON the interface itself (confirmed in the compiled BIR — typeDef's own
	// interfaces-collection drops the FunctionN supertype off ANY interface def, `bt is TypeNode.Fn -> null`,
	// but the fake override still lands in the interface's OWN `methods`). So a call's resolved `declaringClass`
	// for `invoke` on a KProperty0/1-typed receiver is KProperty0/1 itself, never Function0/1 — the ordinary
	// member-call path below emits a plain `callInstance ownerType:kotlin.reflect.KProperty0/1[…] method:invoke`,
	// which `propertyRef`'s lifted class implements directly (mirrors JVM's `PropertyReferenceImpl.invoke() =
	// get()`). bir2cir's CharCodeInvokeLowering only rewrites an `ownerType:kotlin.Function.../KFunction...`
	// call, so it never touches this one.
	// A SAME-MODULE `inline fun` (body present in THIS run) taking ANY lambda arg is source-inlined (AXIS ①): emit the
	// generic `callInline` node and bir2cir splices the raw-BIR body (resolved from `InlineBirStash.Index`) in-context.
	// The lambda ARGS split per-modifier at emit time (AXIS ②, in inlineSpliceCallSameModule): a normal/crossinline
	// lambda -> a spliceable carrier, a noinline lambda -> a real delegate temp. No escape analysis. Gated via
	// `callNeedsSplice` so the suspendCoroutine* intrinsic carve-out is respected here too. (A lambda-less inline call —
	// or a carved-out intrinsic — falls through to the ordinary member-call path.)
	// #87: route inline-splice on the RESOLVED declaration. An INHERITED inline member is a fake override with
	// `body == null`, so a raw `callee.body != null` test would misroute a SAME-module inherited inline call to the
	// cross-module member path below (747). The real declaration carries the body iff it is same-module (kotc holds
	// bodies only for this-run decls; a cross-module base's real decl is also body-less), so routing on `inlineDecl`
	// sends a same-module inherited inline fn to the same-module splice path and a cross-module one to the cross-module
	// path — each matching where emitOwnerfulInlineNode now keys the [KotlinInline] owner (the real declaring class).
	val inlineDecl = callee.let { if (it.isFakeOverride) it.resolveFakeOverride() ?: it else it }
	if (inlineDecl.body != null && callNeedsSplice(call)) return inlineSpliceCallSameModule(call)

	// `Delegates.observable/vetoable/notNull(…)` is NOT intercepted: it resolves to the REAL stdlib
	// `Delegates.observable`/`vetoable`/`notNull` (emitted into DotKt.Stdlib.dll — each returns a real
	// `ReadWriteProperty<Any?,V>`: an `ObservableProperty` subclass or `NotNullVar`) and flows through the
	// ordinary top-level-call path. The delegate-access sites dispatch getValue/setValue on the real generic
	// interface (see the `by lazy`-parallel routing above). No compiler-synthesized delegate class.
	// `by lazy { … }` is NOT intercepted: the `kotlin.lazy(initializer)` call resolves to the real stdlib
	// `lazy()` actual (returns `UnsafeLazyImpl(initializer)`, a pure-Kotlin `Lazy<T>`) and flows through the
	// ordinary top-level-call path below. No System.Lazy construction here (that is CLR knowledge; layer purity).

	if (name == "compareTo") {
		val recv = dispatchReceiver(call)
		val arg = regularArgs(call).firstOrNull()
		val ec = recv?.type?.classifierOrNull?.owner as? IrClass
		if (recv != null && arg != null && ec?.kind == ClassKind.ENUM_CLASS) {
			fun ord(e: IrExpression): String = if (isRichEnum(ec))
				"""{"k":"field","ownerType":${fqnJson(typeName(ec))},"recv":${expr(e)},"name":"__ordinal"}"""
			else """{"k":"enumOrdinal","e":${expr(e)}}"""
			return """{"k":"binOp","op":"-","lhs":${ord(recv)},"rhs":${ord(arg)}}"""
		}
		// A DIRECT primitive `Double/Float.compareTo(y)` is not special-cased here (Kotlin's TOTAL
		// order — `-0.0 < 0.0`, NaN largest, `NaN.compareTo(NaN) == 0` — differs from System.Double.CompareTo). kotc
		// emits the FAITHFUL member call (falls through to the plain callInstance path -> `kotlin.Double.compareTo`)
		// and bir2cir recognizes the Double/Float owner and routes to the stdlib clrDoubleCompare/clrFloatCompare
		// total-order body BEFORE its primitive-compareTo -> System.Double.CompareTo routing. The ENUM branch stays.
	}
	// A PRIMITIVE `x.compareTo(y)` and a `kotlin.Comparable.compareTo` (the `<`/`>`/`<=`/`>=` desugaring on a
	// bounded generic `<T : Comparable<T>>`) are not intercepted here (layer purity): kotc emits the PLAIN
	// member call (`callInstance kotlin.Int.compareTo` / `callInstance kotlin.Comparable.compareTo`, carrying the
	// receiver's static type on the recv node's `retType`/`elem` and the type-param constraints). bir2cir derives the
	// CLR form — a primitive owner -> `clrInstance System.<Prim>.CompareTo`; a @ClrTypeAlias("System.IComparable")
	// owner -> a `constrained.` `System.IComparable<T>::CompareTo` (its ComparableConstrain pass, reusing the
	// value-type/constrained-dispatch knowledge it already owns). The `System.IComparable`/`constrained.` decision
	// is a Kotlin<->CLR relation and lives in bir2cir, not this frontend.

	// NOTE: `reified` gets NO special handling here — it is deliberately never inspected. The CLR has reified
	// generics, so `reified` is pure decoration: a generic function (reified or not) is just emitted as a .NET
	// generic method, and a body that uses `T::class`/`x is T`/`x as T` lowers to `ldtoken !!0`/`isinst !!0`
	// like any other generic-method body. (On the JVM `reified` exists ONLY to drive call-site inlining around
	// erasure; that whole machine is absent here.) See [[clr-not-jvm-discard-jvmisms]].

	// `T::class.simpleName`/`.qualifiedName` is NOT intercepted here (layer purity): kotc emits the PLAIN Kotlin
	// property read `kotlin.reflect.KClass::get_simpleName`/`get_qualifiedName` (via the ordinary member-property
	// path below), and bir2cir's KClassMemberBinding derives the CLR resolution — a `clrPropGet` on `System.Type`
	// (`Name`/`FullName`). The `System.Type` knowledge (which BCL member a KClass member maps to) is a Kotlin<->CLR
	// relation and lives in bir2cir, not in this frontend.

	// The scope functions (let/run/with/apply/also) and use{} are @kotlin.internal.InlineOnly cross-module
	// inline+lambda funs: they route through the generic owner-less `callInline` node at the injected-top-level
	// dispatch below (bir2cir splices their [KotlinInline] raw-BIR payloads off the ref.dll) — NOT special-cased here.

	// `repeat(n) { i -> body }` is NOT special-cased (#75 — the dedicated inlineRepeat splicer is retired). It flows
	// through the general inline gates like any other inline+lambda fn: a LITERAL lambda (AXIS ①) hits the owner-less
	// `callInline` gate below (payload `kotlin.repeat` off the ref.dll — bir2cir wraps the counted loop), and a
	// callable-ref / non-lambda action (`repeat(n, ::fn)` — not an IrFunctionExpression) falls through to the plain
	// `callStatic kotlin.repeat`, which bir2cir's RepeatInlineLowering re-emits as a delegate counter loop.

	// Collection/array factories (`listOf`/`setOf`/`mapOf`/`arrayOf`/`intArrayOf`/`arrayOfNulls`/…) are not
	// recognized here: kotc emits the plain top-level `callStatic kotlin.collections.listOf(...)` (the faithful IR;
	// the vararg argument itself rides as a `newArray` node). bir2cir reads the `@kotlin.clr.ClrCollectionFactory`
	// (kind list/set/map) / `@kotlin.clr.ClrArrayFactory` (vararg/sized) marker off each stdlib factory function on
	// the ref.dll and re-emits the same `{k:newList/newSet/newMap/newArray/newArraySized}` construction node — the
	// element/key/value types from the call's `typeArgs`, the elements from the vararg arg. The `mapOf(a to b)`
	// literal-split (and its "do NOT force-split a non-literal Pair" guard — `mapOf(pairVar)` stays a real call)
	// is bir2cir's.

	// Unsigned<->signed byte-array reinterpret (#76) — `UByteArray.toByteArray()` / `ByteArray.toUByteArray()` — is
	// NOT lowered here: it is a CLR-representation fact ("UByteArray IS byte[]"), so kotc emits the FAITHFUL top-level
	// extension call and bir2cir re-emits the reinterpret `cast` keyed on the resolved receiver identity
	// (FaithfulHintRecognition, M9). The Kotlin<->CLR relation lives there, not in kotc.

	// `e!!` (not-null assertion). Kotlin `x!!` throws NullPointerException IMMEDIATELY when x is null,
	// regardless of how the result is used (stored, discarded, or dereferenced). Both operand kinds bind
	// the operand to a temp ONCE (it may have side effects), null-test it, and throw kotlin.NullPointerException
	// on null; the non-null value is yielded otherwise. A value-type-nullable operand (`Int?` = `Nullable<T>`)
	// tests via HasValue and unwraps .Value — a bare pass-through would leave a `Nullable<T>` STRUCT where the
	// use site consumes the bare value (`n!! + 1` -> InvalidProgram; `n!!.toLong()` reads garbage). A
	// reference-nullable operand tests via objEq-null (mirrors the requireNotNull/checkNotNull reference path
	// in bir2cir's PreconditionLowering) — a bare pass-through would let a null surface only as a later
	// NullReferenceException at a deref (wrong exception type + site) and NEVER throw for a stored/discarded
	// `x!!`. `!!` throws kotlin.NullPointerException; the precondition helpers throw IllegalArgument/State.
	if (name == "CHECK_NOT_NULL") {
		val arg = call.arguments.filterNotNull().first()
		val velem = nullableElem(arg.type)
		val nv = "__nn${scopeCounter++}"
		val nvLoc = """{"k":"local","name":${str(nv)}}"""
		val throwNpe = throwExpr(newExc("kotlin.NullPointerException", null))
		if (velem != null) {
			return """{"k":"valueBlock","stmts":[{"k":"var","name":${str(nv)},"type":${TypeNode.Nullable(velem).toJson()},"init":${expr(arg)}}],"result":{"k":"cond","cond":{"k":"nullableHasValue","elem":${velem.toJson()},"e":$nvLoc},"then":{"k":"nullableValue","elem":${velem.toJson()},"e":$nvLoc},"else":$throwNpe}}"""
		}
		// reference (or objEq-testable: generic `T?`) operand: bind once, `(t != null) ? t : throw` (value in
		// `then`, mirroring the value-type path above and bir2cir's PreconditionLowering reference shape). objEq
		// boxes a generic local before the null-test, so a HasValue==false `Nullable<T>` reads as a genuine null
		// and throws. (Unsigned `UInt?`/`UByte?`/... take the value-type HasValue/Value branch ABOVE: #118 -- they
		// ARE value types on the CLR (`Nullable<uint>`), so `nullableElem` includes them via `isPrimitiveOrUnsigned`;
		// a bare pass-through would leave a `Nullable<uint>` STRUCT at the use site, the #56 struct-consumer issue.)
		val nullConst = """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}"""
		return """{"k":"valueBlock","stmts":[{"k":"var","name":${str(nv)},"type":${birType(arg.type).toJson()},"init":${expr(arg)}}],"result":{"k":"cond","cond":{"k":"unaryOp","op":"!","e":{"k":"objEq","lhs":$nvLoc,"rhs":$nullConst}},"then":$nvLoc,"else":$throwNpe}}"""
	}

	// Value-position primitive `rangeTo`/`rangeUntil` (`a..b` / `a..<b`) is NOT lowered here. kotc emits the
	// FAITHFUL `callInstance kotlin.Int.rangeTo(b)` member call (CLR primitives have no instance methods, but that
	// is a CLR fact); bir2cir (RangeConstructionLowering) MATERIALIZES the stdlib range class — `new IntRange/
	// LongRange/CharRange`, applying the `-1` half-open arithmetic for rangeUntil. Structured for-loops are still
	// counter-lowered in birForLoop (they intercept the range at the IR level before this member call is emitted).

	// `x in a..b` (range membership) is NOT lowered here. kotc emits the FAITHFUL `contains` member call on the
	// range receiver (`callInstance <range>.contains(x)`) by identity — NO comparison synthesis and NO FQN gate,
	// so a USER type with `operator fun rangeTo`+`contains` stays a real method dispatch (the bare-name lowering
	// here MISCOMPILED it to primitive comparisons). bir2cir (RangeMembershipLowering) lowers `x in a..b` /
	// `x in a until b` to the short-circuit `(x >= a && x <op> b)` fast path FQN-keyed — only when the range is an
	// un-materialized primitive `kotlin.<Prim>.rangeTo/rangeUntil` — binding `x` ONCE (a side-effecting operand
	// must not run in both comparison legs). The Kotlin<->CLR range relation lives in bir2cir.

	// Enum rich API: Color.values()/entries -> Enum.GetValues<T>(); Color.valueOf(s) -> Enum.Parse<T>(s).
	(callee.parent as? IrClass)?.takeIf { it.kind == ClassKind.ENUM_CLASS }?.let { ec ->
		// Rich enum -> the synthesized static values()/valueOf() methods on the class.
		if (isRichEnum(ec)) {
			if (name == "values" || callee.correspondingPropertySymbol?.owner?.name?.asString() == "entries")
				return """{"k":"callStatic","owner":${fqnJson(ec.name.asString())},"method":"values","args":[]}"""
			if (name == "valueOf") return """{"k":"callStatic","owner":${fqnJson(ec.name.asString())},"method":"valueOf","args":[${expr(regularArgs(call).first())}]}"""
		}
		// Basic enum -> the semantic enumValues/enumParse node carrying the enum's FAITHFUL FQN identity (a
		// structured Type, never the banned `@Name` type-token). bir2cir/ilemit resolve it to the local enum type,
		// exactly as the reified `enumValues<T>()` path does (EnumIntrinsicLowering re-emits the same node shape).
		if (name == "values" || callee.correspondingPropertySymbol?.owner?.name?.asString() == "entries")
			return """{"k":"enumValues","type":${fqnJson(ec.name.asString())}}"""
		if (name == "valueOf") return """{"k":"enumParse","type":${fqnJson(ec.name.asString())},"arg":${expr(regularArgs(call).first())}}"""
	}
	// The top-level reified enum intrinsics `enumValues<T>()` / `enumValueOf<T>(name)` / `enumEntries<T>()`
	// / `enumEntriesIntrinsic<T>()` are NOT recognized here: kotc emits the FAITHFUL top-level call
	// `callStatic owner:null method:<the callee's bare name> typeArgs:[T] args:[…]` (the plain Kotlin fact) via the
	// general call path. bir2cir's EnumIntrinsicLowering re-emits the same BIR vocabulary — a rich enum's synthesized static
	// `values()`/`valueOf()`, or the semantic `enumValues`/`enumParse` node for a basic/generic-param T — deriving
	// rich-vs-basic from the enum type's emitted shape (a local rich enum carries `enumRich:true`). "This call is
	// enumValues" is a Kotlin<->CLR relation, so it lives in bir2cir. (The `.name`/`.ordinal` handling below asks
	// the IR — `ClassKind.ENUM_CLASS` — not an FQN table, so it stays here.)
	// `c.code` (Char -> Int code point) is NOT recognized here: kotc emits the FAITHFUL top-level extension-property
	// getter call `callStatic owner:null method:get_code sig:[kotlin.Char] args:[<char>]` (the plain Kotlin fact) via
	// the general property path. bir2cir's CharCodeInvokeLowering re-emits the `{k:conv, to:kotlin.Int}` node (a
	// genuine primitive IL op — the char value AS an int, distinct from `.toInt()`'s @ClrConv) off that faithful
	// call, resolving `get_code`->kotlin.CharCodeKt against the ref.dll. The Kotlin<->CLR relation lives in bir2cir.
	// c.name -> toString() (enum name); c.ordinal -> (int)c.  Rich enum -> the __name/__ordinal fields.
	dispatchReceiver(call)?.takeIf { (it.type.classifierOrNull?.owner as? IrClass)?.kind == ClassKind.ENUM_CLASS }?.let { rc ->
		val rec = (rc.type.classifierOrNull?.owner as? IrClass)
		if (rec != null && isRichEnum(rec)) when (callee.correspondingPropertySymbol?.owner?.name?.asString()) {
			"name" -> return """{"k":"field","ownerType":${fqnJson(rec.name.asString())},"recv":${expr(rc)},"name":"__name"}"""
			"ordinal" -> return """{"k":"field","ownerType":${fqnJson(rec.name.asString())},"recv":${expr(rc)},"name":"__ordinal"}"""
		}
		when (callee.correspondingPropertySymbol?.owner?.name?.asString()) {
			"name" -> return """{"k":"objMethod","method":"toString","recv":${expr(rc)}}"""
			"ordinal" -> return """{"k":"enumOrdinal","e":${expr(rc)}}"""
		}
	}

	// `a to b` and Pair/Triple/IndexedValue `componentN()` are NOT recognized here: these are real
	// emitted stdlib types with real members — the infix `to` (body `Pair(this, that)`) and the data-class
	// component1()/component2()/component3() operators are materialized IR declarations. kotc emits the plain call
	// (faithful IR) and it resolves against the real stdlib surface; no marker is needed (unlike conv/factories,
	// which synthesize CLR-shaped nodes). So `5 to 6`, `val (a, b) = pair`, `t.component1()` all fall through to
	// the ordinary call path.
	// Map-entry destructuring `entry.component1()/.component2()` is NOT lowered to KeyValuePair.Key/.Value here:
	// map entries are real `kotlin.collections.Map.Entry` objects (rt ClrMutableMapEntry; both Map/MutableMap alias
	// IDictionary), so the destructure components emit as the PLAIN Kotlin extension calls and resolve like any
	// stdlib call. Reading a ref object as a KeyValuePair struct would reinterpret memory -> garbage values (and
	// KeyValuePair is CLR knowledge the layer rules forbid inside kotc).

	// Invoking a function-typed value `f(x)` -> delegate `Invoke` (Func/Action) is NOT recognized here: kotc emits
	// the FAITHFUL `callInstance ownerType:kotlin.FunctionN[..]/kotlin.reflect.KFunctionN[..] method:invoke` member
	// call (the plain Kotlin fact) via the general instance-call path. bir2cir's CharCodeInvokeLowering re-emits the
	// `{k:delegateInvoke}` node off that faithful call — deriving `funcType` from the FunctionN owner's type args
	// (params = args[..n-1], ret = args[n]). A function-typed value IS a delegate at the CLR level; that Kotlin<->CLR
	// relation lives in bir2cir. (Includes a callable-reference value `(c::method)(x)` whose type is `KFunctionN`.)
	// MutableList/MutableCollection mutation members (`add`/`remove`/`clear`/`removeAt`) -> the BCL List<T>
	// instance method. Kotlin collections lower to System.Collections.Generic.List<T>; these are instance calls,
	// not collection extension ops (the real stdlib `map`/`filter`/`mapTo` bodies — which build an ArrayList via
	// `.add(...)` — run on the BCL list).
	// Array indexing `a[i]` / `a[i] = v` (the `get`/`set` operators on Array/primitive arrays).
	if (callee.isOperator && (name == "get" || name == "set")) {
		val recv = dispatchReceiver(call)
		if (recv != null && isArrayType(recv.type)) {
			// No `elem` field: bir2cir DERIVES the element off the array operand's (now faithful) type. kotc emits
			// only the faithful get/set intrinsic + the array operand.
			val a = regularArgs(call)
			return if (name == "get") """{"k":"arrayGet","array":${expr(recv)},"index":${expr(a[0])}}"""
			else """{"k":"arraySet","array":${expr(recv)},"index":${expr(a[0])},"value":${expr(a[1])}}"""
		}
		// String indexing `s[i]` is NOT lowered here: `kotlin.String.get(index)`
		// carries @ClrIntrinsic("get_Chars") (runtime/stdlib/clr/builtins/String.kt); kotc emits the plain operator
		// `get` member call on kotlin.String and bir2cir's MemberCallSubstitution rewrites it to
		// `clrInstance System.String.get_Chars` off the ref.dll — the Kotlin<->CLR relation lives in bir2cir, not kotc.
		// kotlin.* List/Map indexing `list[i]`/`m[k]` is NOT intercepted: in FIR it's already an operator call to
		// `get`/`set` — fall through to the ordinary call path so it emits as a real kotlin.* `get`/`set` call.
		// Injected .NET indexer `c[i]` / `c[i] = v` -> the DEFAULT INDEXED PROPERTY of the constructed .NET type.
		// kotc emits the FAITHFUL Kotlin get/set operator identity (`method:"get"/"set"`) plus an index marker
		// (`"prop":"index-get"/"index-set"`, extending step 3's accessor-KIND mechanism); it does NOT bake the CLR
		// slot name. bir2cir's NetInteropBinding reflects the .NET type's default indexed property off the refs (its
		// DefaultMember / `[IndexerName]` name) -> its `get_`/`set_` accessor method, emitting the plain `clrInstance`
		// call — byte-identical to the old hardcoded `get_Item`/`set_Item` for the standard case, but correct for a
		// custom-named indexer. The receiver's type carries the element type arg (`Collection<Int>`), so the
		// constructed `clrg:...[int]` resolves the substituted accessor.
		val ixOwner = (callee.takeIf { it.isFakeOverride }?.resolveFakeOverride()?.parent as? IrClass) ?: declaringClass
		if (recv != null && ixOwner != null && isExternalNetType(ixOwner)) {
			val mt = birType(recv.type); val a = regularArgs(call)
			// The get accessor returning a generic param (`IList<T>.get` -> T) reports the SUBSTITUTED ret (gp:T):
			// ilemit then hands back gp:T (matching the stack), so the value<->collection boundary box/unbox is
			// correctly typed (else a value-type instantiation NullRefs/garbages). Needs ClrRef("gp:") -> MapType.
			val retH = birType(call.type)
			// `virtual` for the fallback where bir2cir cannot resolve the owner and the raw `method:"get"/"set"` node
			// reaches ilemit (an open/override operator get/set must callvirt) — same rationale as the .NET-interop
			// callInstance path below (#139). bir2cir drops it when it reshapes the indexer to a clrInstance accessor.
			val ixVirtual = isVirtualInstanceCall(call, callee)
			return if (name == "get")
				"""{"k":"callInstance","virtual":$ixVirtual,"ownerType":${str(mt)},"method":"get","prop":"index-get","argTypes":[${birType(a[0].type).toJson()}],"ret":${str(retH)},"recv":${expr(recv)},"args":[${expr(a[0])}]${superTag(call)}}"""
			else
				"""{"k":"callInstance","virtual":$ixVirtual,"ownerType":${str(mt)},"method":"set","prop":"index-set","argTypes":[${birType(a[0].type).toJson()},${birType(a[1].type).toJson()}],"ret":${fqnJson("kotlin.Unit")},"recv":${expr(recv)},"args":[${expr(a[0])},${expr(a[1])}]${superTag(call)}}"""
		}
	}

	// #60 (W1): a cross-module inline MEMBER (`body==null`, a DISPATCH receiver present) taking ANY lambda arg (AXIS ①)
	// MUST be source-inlined — a facadegen-injected DotKt member AND a klib stdlib member alike. kotc is body-BLIND here
	// (the klib is metadata-only; the [KotlinInline] payload lives on the ref.dll), so it emits the owner-ful `callInline`
	// UNCONDITIONALLY and bir2cir — which holds the payload — makes the splice-or-fail-loud eligibility decision (it
	// resolves the payload off the ref.dll `InlineCandidates`, and its §4.3 rebinds the payload's `{k:this}` to the
	// caller-provided `recvs.dispatch`). This MUST run BEFORE the CLR-interop member block below: that block fires for ANY
	// injected .NET owner (`clrName(declaringClass) != null`) and would otherwise emit a plain `callInstance` + a REAL
	// delegate for the block, whose non-local `return` returns from the DELEGATE, not the caller — a SILENT miscompile.
	// The member-EXTENSION dual-receiver (#23) shape rides through too (both receivers carried): bir2cir splices the
	// SOUND pure-extension idiom (body reads only the extension `this`) and FAILS LOUD on a body that reads the dispatch
	// receiver (a `{k:this}`) — converting the old silent #23 gap to loud until W2 co-binds both receivers.
	if (inlineDecl.body == null && callNeedsSplice(call) && dispatchReceiver(call) != null)
		return emitOwnerfulInlineNode(call)

	// NEUTRAL .NET-interop fact-carrier selector (A2/#61 — REALIZED; NOT a .NET call-SHAPE decision). This block
	// decides NO CLR shape: it emits ONLY plain `callStatic`/`callInstance` nodes carrying frontend FACTS —
	// static-ness (callStatic vs callInstance, from receiver presence), the accessor KIND (`prop:"get"/"set"`,
	// from correspondingPropertySymbol), the indexed-access fact (`prop:"index-get"/"index-set"`), `typeArgs`+
	// declared `shapeTypes`, `argTypes`/`ret`, and the constructed-owner IDENTITY (the `memberType` supertype
	// walk). EVERY .NET shape — `clrStatic`/`clrInstance`/`clrPropGet`/`clrPropSet`/`clrGeneric*`, the indexer's
	// `get_Item`/`[IndexerName]` accessor slot, `op_X` operators — is decided BELOW the kotc boundary by
	// bir2cir's `NetInteropBinding`, which re-detects the .NET owner itself (ResolveNetType off the ref dlls),
	// independent of this gate. What differs from the plain-Kotlin member paths below is only the fact-carrier
	// DIALECT (`ownerType`+`argTypes`+`ret`+`prop` marker vs `owner`+`sig`+`retHint`) — the kotc↔bir2cir
	// serialization contract that routes a node to `NetInteropBinding` (ownerType-keyed) vs `MemberCallSubstitution`
	// (owner-keyed) — NOT a CLR decision. The `clrName` gate is a pure ORIGIN fact ("this owner is facadegen-
	// injected", read off the IR ClassId — a frontend fact kotc is allowed to hold, like `isExternalNetType`), NOT
	// an interpretation of `@Clr*` metadata or a BCL shape. The sole dialect EXCEPTION emitted here is `clrEventGet`
	// (a .NET event has no plain-Kotlin call form — CLR-only vocab, by design). An INHERITED .NET member (e.g.
	// `appError.Message`) is a fake-override whose `parent` is the Kotlin subclass, so resolve through the fake
	// override to the real .NET declaring type. A `kotlin.*` stdlib owner resolves to null here and FALLS THROUGH to
	// the plain Kotlin member-call path below (bir2cir substitutes it from the ref.dll).
	val clrTypeName = declaringClass?.let { clrName(it) }
		?: (callee.takeIf { it.isFakeOverride }?.resolveFakeOverride()?.parent as? IrClass)?.let { clrName(it) }
		// A synthesized companion of an injected .NET type holds its STATIC members (`App.Start`) -> a static call
		// on the .NET type itself.
		?: declaringClass?.takeIf { it.isCompanion }?.let { it.parent as? IrClass }?.let { clrName(it) }
	val clrType = clrTypeName?.let { TypeNode.Fqn(it) }
	if (clrType != null) {
		val recv = dispatchReceiver(call)
		// A synthesized companion of a normal injected .NET class represents that class's CLR statics. A genuine Kotlin
		// `object`, however, is an instance singleton: keep its IrGetObjectValue receiver in BIR (`Owner.INSTANCE`) and
		// let bir2cir decide whether the referenced owner is actually a CLR static class or an emitted Kotlin object.
		// Treating every object receiver as CLR static here erased the receiver of cross-module `Dispatchers.Default`.
		val injectedStaticCompanion = declaringClass?.isCompanion == true &&
			(declaringClass.parent as? IrClass)?.let { clrName(it) } != null
		val isStatic = recv == null || injectedStaticCompanion
		// A NON-static callInstance emitted here is normally reshaped to a `clrInstance` by bir2cir's
		// NetInteropBinding (which resolves the owner off the .NET refs) — where `virtual` is irrelevant. But a
		// DotKt library consumed AS KOTLIN whose owner bir2cir cannot resolve (netType == null -> left un-reshaped)
		// reaches ilemit as a raw `callInstance`; ilemit reads `virtual` to pick call vs callvirt. So stamp it here
		// exactly like the plain Kotlin member-call path: virtual unless FINAL and not an override. Without it ilemit
		// would default to a non-virtual `call`, mis-dispatching an `open`/`override` member (#139).
		val clrCallVirtual = isVirtualInstanceCall(call, callee)
		// Address the member on the CONSTRUCTED .NET type (`clrg:Collection[int]`) so a member of a generic
		// instantiation resolves. Two cases: (1) the receiver's own type IS the .NET type; (2) the member is
		// INHERITED from a .NET base (receiver is a Kotlin subclass) -> use the subclass's .NET supertype,
		// which carries the concrete type args (`class C : Collection<Int>`).
		val recvClass = recv?.type?.classifierOrNull?.owner as? IrClass
		// The REAL .NET declaring type (resolve the fake override; `declaringClass` would be the subclass).
		val declClass = (callee.takeIf { it.isFakeOverride }?.resolveFakeOverride()?.parent as? IrClass) ?: declaringClass
		val memberType = when {
			isStatic -> clrType
			recvClass != null && isExternalNetType(recvClass) -> birType(recv.type)
			// A type-PARAM receiver (`destination: C` where `C : MutableCollection<T>`, e.g. filterTo's body) has no
			// recvClass -> use the type param's @Clr-bound BOUND with its args (clrg:ICollection[T]), not the raw
			// clrName (System.Collections.Generic.ICollection without `1 -> ResolveType fails).
			else -> (recvClass?.superTypes ?: (recv.type.classifierOrNull?.owner as? org.jetbrains.kotlin.ir.declarations.IrTypeParameter)?.superTypes)
				?.firstOrNull { it.classifierOrNull?.owner == declClass }?.let { birType(it) } ?: clrType
		}
		// A .NET event is NOT rewritten to an `add_<E>`/`remove_<E>` call here. It is surfaced as a
		// `kotlin.clr.ClrEvent<T>` property and consumed via `subscribe`; kotc emits the plain subscribe call (handled
		// at the top of this function), and bir2cir's ClrEventSubscriptionBinding binds it to add + close-token remove.
		// No `add_`/`remove_` naming, no clrEventAdd
		// in kotc — the Kotlin<->CLR event relation is bir2cir's (layer purity).
		// A generic .NET method (`Unsafe.SizeOf<T>()`, `Activator.CreateInstance<T>()`) -> resolve the open
		// generic-method definition by name + type-arity + parameter shapes, then MakeGenericMethod with the
		// call's type args. The CLR has reified generics, so this is just an ordinary generic-method call (no
		// erasure dance) — see [[clr-not-jvm-discard-jvmisms]]. Static -> clrGenericStatic, instance -> ...Instance.
		if (callee.typeParameters.isNotEmpty()) {
			val targs = callee.typeParameters.indices.map { call.typeArguments.getOrNull(it) }
			if (targs.all { it != null }) {
				val taJson = targs.joinToString(",") { birType(it!!).toJson() }
				val member = name
				val anySlotTag = if (isAnySlotMethod(callee)) ""","anySlot":true""" else ""
				// A generic MEMBER extension (`class C { fun <R> T.f() }`): the `__self` receiver is the .NET method's
				// first param -> prepend its value + shape so by-shape overload resolution and the call line up.
				val gExt = if (!isStatic) extensionReceiver(call) else null
				val shapeParams = (if (gExt != null) listOf(gExt.type) else emptyList()) + regularParams(callee).map { it.type }
				// kotc emits the DECLARED parameter types as PURE-KOTLIN `birType` identities (`shapeTypes`); bir2cir
				// DERIVES the ilemit `shapes` overload-matcher tokens (the .NET simple names Int64/SByte/… + gp/
				// generic/ienum/func:N) off the @ClrTypeAlias index and drops `shapeTypes`. No CLR-shape knowledge here.
				val shapeTypes = shapeParams.joinToString(",") { birType(it).toJson() }
				val argsJson = (listOfNotNull(gExt) + regularArgs(call)).joinToString(",") { expr(it) }
				// A `suspend` generic .NET-member callee carries the `"suspendCall":true` FACT for bir2cir's deferred
				// Task/await lowering, exactly like the non-generic call paths (suspendCallTag) — otherwise a generic
				// .NET-member suspend call would silently drop out of the suspension lowering. (latent ⑤.)
				// A2 (#61): a PLAIN call by identity carrying the generic FACTS (typeArgs + declared shapeTypes);
				// bir2cir's NetInteropBinding resolves the owner off the .NET refs and shapes it to clrGenericStatic/
				// clrGenericInstance (the `typeArgs` presence is the generic signal).
				return if (isStatic)
					"""{"k":"callStatic","ownerType":${clrType!!.toJson()},"method":${str(member)},"typeArgs":[$taJson],"shapeTypes":[$shapeTypes],"args":[$argsJson]${suspendCallTag(callee)}$anySlotTag}"""
				else
					"""{"k":"callInstance","virtual":$clrCallVirtual,"ownerType":${memberType!!.toJson()},"method":${str(member)},"typeArgs":[$taJson],"shapeTypes":[$shapeTypes],"recv":${expr(recv!!)},"args":[$argsJson]${suspendCallTag(callee)}$anySlotTag${superTag(call)}}"""
			}
		}
		val prop = callee.correspondingPropertySymbol?.owner
		if (prop != null) {
			// A `kotlin.clr.ClrEvent<T>` property read is legal ONLY as the receiver of `.subscribe(h)`, where
			// clrEventReceiverOk is set. A bare read (`val e = w.Changed`) would emit a
			// `clrPropGet get_<Event>` that no bir2cir rule strips -> a distant, diagnostic-free downstream failure.
			// A .NET event is not a first-class value, so reject it here at the source with a kotc compile error.
			if (!clrEventReceiverOk && callee === prop.getter
				&& callee.returnType.classFqName?.asString() == "kotlin.clr.ClrEvent") {
				hadError = true
				messageCollector?.report(CompilerMessageSeverity.ERROR,
					"a .NET event ('${prop.name.asString()}') is not a first-class value: it may only be used with " +
						"'.subscribe(handler)', not be read/assigned",
					locationOf(call))
				return """{"k":"unsupportedExpr","of":"clr-event-read-outside-subscription: ${prop.name.asString()}"}"""
			}
			// A2 step 3: the property's OWN Kotlin name IS the .NET slot identity (facadegen injects the member under
			// its .NET name), so kotc reads NO CLR name here — it emits the bare property name + the accessor KIND
			// (`"prop":"get"/"set"`, a frontend fact from correspondingPropertySymbol). bir2cir's NetInteropBinding
			// applies the .NET `get_`/`set_` accessor convention off the refs.
			val pn = prop.name.asString()
			val recvJson = if (isStatic) "null" else expr(recv!!)
			// A restored MEMBER extension property (`class C { val T.p }`): no .NET property exists — it's a
			// `get_p(__self)`/`set_p(__self, v)` method on the dispatch type, the extension receiver as `__self`.
			// A2 (#61): a PLAIN instance call by identity carrying the accessor KIND; bir2cir's NetInteropBinding
			// finds NO .NET property `p` (it's a synthetic accessor method) and applies the convention -> a clrInstance
			// get_p/set_p method call.
			extensionReceiver(call)?.let { pExt ->
				return if (callee === prop.setter)
					"""{"k":"callInstance","virtual":$clrCallVirtual,"ownerType":${memberType!!.toJson()},"method":${str(pn)},"prop":"set","argTypes":[${birType(pExt.type).toJson()},${birType(regularArgs(call).first().type).toJson()}],"ret":${fqnJson("kotlin.Unit")},"recv":$recvJson,"args":[${expr(pExt)},${expr(regularArgs(call).first())}]${superTag(call)}}"""
				else """{"k":"callInstance","virtual":$clrCallVirtual,"ownerType":${memberType!!.toJson()},"method":${str(pn)},"prop":"get","argTypes":[${birType(pExt.type).toJson()}],"ret":${birType(callee.returnType).toJson()},"recv":$recvJson,"args":[${expr(pExt)}]${superTag(call)}}"""
			}
			// A2 (#61): a `kotlin.clr.ClrEvent<T>` read is CLR-ONLY vocabulary — a .NET event has no plain-Kotlin
			// call form (it exposes add_/remove_, not a get_); facadegen injects it purely to typecheck, so kotc
			// LOWERS it directly to a DEDICATED dialect node `clrEventGet` (the ClrEvent<T> handle) — NOT the
			// bir2cir-produced `clrPropGet` (which after A2 means a real .NET property). It exists ONLY to feed a
			// `subscribe`: bir2cir's ClrEventSubscriptionBinding consumes the `clrEventGet + call` pair
			// into an add_/remove_ accessor, so it never reaches ilemit (a bare event read is rejected above). Every
			// OTHER property is a plain Kotlin-shaped access -> emit the get_/set_ accessor CALL by identity;
			// NetInteropBinding shapes it to clrPropGet/clrPropSet (a .NET property OR field) off the refs.
			if (callee.returnType.classFqName?.asString() == "kotlin.clr.ClrEvent") {
				return """{"k":"clrEventGet","type":${memberType!!.toJson()},"name":${str(pn)},"static":$isStatic,"recv":$recvJson}"""
			}
			val propCallKind = if (isStatic) "callStatic" else "callInstance"
			val propRecvField = if (isStatic) "" else ""","recv":$recvJson"""
			// A non-static property-accessor callInstance carries `virtual` too (moot once bir2cir reshapes it to
			// clrPropGet/clrPropSet; consistent with the other .NET-interop callInstance nodes — #139).
			val propVirtualField = if (isStatic) "" else ""","virtual":$clrCallVirtual"""
			return if (callee === prop.setter)
				"""{"k":"$propCallKind"$propVirtualField,"ownerType":${memberType!!.toJson()},"method":${str(pn)},"prop":"set","argTypes":[${birType(regularArgs(call).first().type).toJson()}]$propRecvField,"args":[${expr(regularArgs(call).first())}]${superTag(call)}}"""
			else """{"k":"$propCallKind"$propVirtualField,"ownerType":${memberType!!.toJson()},"method":${str(pn)},"prop":"get","argTypes":[],"ret":${birType(callee.returnType).toJson()}$propRecvField,"args":[]${superTag(call)}}"""
		}
		val member = name
		val anySlotTag = if (isAnySlotMethod(callee)) ""","anySlot":true""" else ""
		val argsJson = regularArgs(call).joinToString(",") { expr(it) }
		// kotc emits the PLAIN Kotlin return type; a `suspend` callee is marked by `suspendTag` only (the Task/await
		// lowering is a deferred downstream layer). No coroutine ABI (Task<T>) is baked here.
		val ret = birType(callee.returnType).toJson()
		val suspendTag = suspendCallTag(callee)
		// A .NET operator (`Vec2 + Vec2` -> op_Addition) is emitted here as the PLAIN Kotlin operator identity
		// (`callInstance method="plus" recv:<a> args:[<b>]`); bir2cir's NetInteropBinding resolves the owner off
		// the .NET refs, confirms the CLR type declares the `op_X` static, and reshapes it to a `clrStatic op_X`
		// with the receiver prepended. No `op_` naming / receiver-prepend here (layer purity — CLR knowledge is bir2cir's).
		// A .NET extension method `static M(this T self, …)` exposed as a Kotlin extension `fun T.m()` on a @Clr
		// object: it's a STATIC call whose first argument is the extension receiver.
		val extRecv = extensionReceiver(call)
		if (isStatic && extRecv != null) {
			val allArgs = (listOf(expr(extRecv)) + regularArgs(call).map { expr(it) }).joinToString(",")
			val allArgTypes = (listOf(birType(extRecv.type).toJson()) + regularArgs(call).map { birType(it.type).toJson() }).joinToString(",")
			return """{"k":"callStatic","ownerType":${clrType!!.toJson()},"method":${str(member)},"argTypes":[$allArgTypes],"ret":$ret,"args":[$allArgs]$suspendTag$anySlotTag}"""
		}
		// A restored MEMBER extension function (`class C { fun T.f() }`): an INSTANCE method on the dispatch receiver
		// (C) whose first .NET param `__self` is the extension receiver -> dispatch on `recv`, prepend the receiver.
		if (!isStatic && extRecv != null && recv != null) {
			val allArgs = (listOf(expr(extRecv)) + regularArgs(call).map { expr(it) }).joinToString(",")
			val allArgTypes = (listOf(birType(extRecv.type).toJson()) + regularArgs(call).map { birType(it.type).toJson() }).joinToString(",")
			return """{"k":"callInstance","virtual":$clrCallVirtual,"ownerType":${memberType!!.toJson()},"method":${str(member)},"argTypes":[$allArgTypes],"ret":$ret,"recv":${expr(recv)},"args":[$allArgs]$suspendTag$anySlotTag${superTag(call)}}"""
		}
		// A2 (#61): a PLAIN static/instance call by the .NET owner's FQN identity; bir2cir's NetInteropBinding
		// resolves the owner off the .NET refs and shapes it (clrStatic/clrInstance). No .NET-shape decision here.
		val (cArgs, cArgTypes) = clrCallArgs(call, callee)
		return if (isStatic)
			"""{"k":"callStatic","ownerType":${clrType!!.toJson()},"method":${str(member)},"argTypes":[$cArgTypes],"ret":$ret,"args":[$cArgs]$suspendTag$anySlotTag}"""
		else
			"""{"k":"callInstance","virtual":$clrCallVirtual,"ownerType":${memberType!!.toJson()},"method":${str(member)},"argTypes":[$cArgTypes],"ret":$ret,"recv":${expr(recv!!)},"args":[$cArgs]$suspendTag$anySlotTag${superTag(call)}}"""
	}

	// Companion-object member -> a static member of the enclosing class (precedes user-property field access).
	// A super-typed companion is a real singleton (<Outer>.InstanceClass) instead: its members are NOT static on the
	// parent, so fall through to the normal instance-call path (receiver = the companion-as-value -> INSTANCE).
	(callee.parent as? IrClass)?.takeIf { it.isCompanion && superTypedCompanion(it.parent as IrClass) == null }?.let { comp ->
		val enclosing = typeName(comp.parent as IrClass)
		val prop = callee.correspondingPropertySymbol?.owner
		if (prop != null) {
			// A companion EXTENSION property (`val Int.seconds` on Duration.Companion) is NEVER a static field —
			// extension properties have no backing field (a cross-module deserialized stub may claim one; trusting
			// it dropped the receiver entirely: `2.seconds` emitted a bare `staticField Duration.seconds`, and the
			// in-module getter path emitted `get_milliseconds` with `"args":[]`). Emit a static call by the
			// property's OWN bare Kotlin identity + a `"prop":"get"/"set"` marker (#78/#81) on the enclosing class
			// with the receiver as the leading arg; `sig` picks the right overload (seconds(Int|Long|Double)).
			// bir2cir shapes the .NET accessor from the stdlib @ClrProperty/@ClrIntrinsic metadata, falling back to
			// kotc's own get_/set_<name> declaration convention when no binding exists.
			val ext = extensionReceiver(call)
			if (ext != null) return if (callee === prop.setter) {
				val args = listOf(ext) + regularArgs(call)
				"""{"k":"callStatic","owner":${fqnJson(enclosing)},"method":${str(prop.name.asString())},"prop":"set"${overloadSigField(callee)},"args":[${args.joinToString(",") { expr(it) }}]}"""
			} else
				"""{"k":"callStatic","owner":${fqnJson(enclosing)},"method":${str(prop.name.asString())},"prop":"get"${overloadSigField(callee)},"args":[${expr(ext)}]${retHint(false, call.type)}}"""
			// A companion COMPUTED property (`val X.Companion.foo: T get() = ...`, no backing field) OR one with a
			// backing field (initializer) but a CUSTOM accessor (`val foo = 7; get() = field + 100`, #89) -> a
			// static call by the property's OWN bare Kotlin identity + a `"prop":"get"/"set"` marker (#78; the SAME
			// A2 convention already used for the restored top-level property, `pn`/`"prop"` above) — NOT the baked
			// `get_`/`set_` slot name and NOT a raw static-field load (that would skip the custom accessor).
			// kotc does not know whether the enclosing class is CLR-bound (a
			// stdlib @ClrTypeAlias owner) or plain Kotlin; bir2cir's MemberCallSubstitution reads the stdlib
			// @ClrProperty/@ClrIntrinsic metadata off the ref.dll by this bare name and shapes the .NET
			// accessor, falling back to kotc's own get_/set_<name> declaration convention when no binding exists.
			return if (callee === prop.setter)
				if (!writesAsStaticField(prop))
					"""{"k":"callStatic","owner":${fqnJson(enclosing)},"method":${str(prop.name.asString())},"prop":"set","args":[${regularArgs(call).joinToString(",") { expr(it) }}]}"""
				else
					"""{"k":"staticFieldSet","ownerType":${fqnJson(enclosing)},"name":${str(prop.name.asString())},"value":${expr(regularArgs(call).first())}}"""
			else if (!readsAsStaticField(prop))
				"""{"k":"callStatic","owner":${fqnJson(enclosing)},"method":${str(prop.name.asString())},"prop":"get","args":[]${retHint(false, call.type)}}"""
			else """{"k":"staticField","ownerType":${fqnJson(enclosing)},"name":${str(prop.name.asString())}}"""
		}
		// A generic companion fun (`Result.Companion.success<T>`) carries its resolved type args — without them
		// the emitted call references the uninstantiated generic method (invalid IL on a generic enclosing class).
		// A companion EXTENSION fun (`fun String.f()` inside a companion object) lowers to a static method whose
		// first param `__self` is the extension receiver (BirEmitterDeclarations extRecv path). The call must pass
		// that receiver as the LEADING arg — matching the declaration signature — else the receiver is dropped and
		// the call arity mismatches the method (#177). Mirrors the member extension-fun path above (~line 903).
		val compExt = extensionReceiver(call)
		val compArgs = (if (compExt != null) listOf(expr(compExt)) else emptyList()) + filledArgs(call)
		return """{"k":"callStatic","owner":${fqnJson(enclosing)},"method":${str(name)}${overloadSigField(callee)}${typeArgsJson(call)},"args":[${compArgs.joinToString(",")}]}"""
	}

		// An INJECTED top-level property (from a DotKt assembly) -> the referenced .NET file class holds it. An
		// EXTENSION property (`val T.p`) surfaces as get_/set_<name>(__self) statics with the extension receiver
		// passed as `__self`; a plain field-backed NON-extension property (`val greeting`) is a STATIC FIELD, so
		// read -> `staticField` / write -> `staticFieldSet` of that referenced file class (#34b). BUT a field-backed
		// property with a CUSTOM accessor (`val x = 41; get() = field + 1`, #103) additionally emits a `get_`/`set_`
		// method on the file class — reading/writing the raw field would SKIP it (a silent cross-module miscompile).
		// (body==null = injected stub.)
		(callee.correspondingPropertySymbol?.owner)?.let { p ->
			// A2 stage 3: read the restored top-level property's .NET file-facade class off its RESOLVED IR
			// `CallableId` (`package` + name).
			val callableId = (p.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)
				?.let { CallableId(it.packageFqName, p.name) }
			if (declaringClass == null) callableId
				?.let { kotc.frontend.clrInjectedTopLevelPropFileClass(it) }?.let { fileClass ->
				val isExt = p.getter?.parameters?.any { it.kind == IrParameterKind.ExtensionReceiver } == true
				if (!isExt) {
					// #103: a field-backed prop with a CUSTOM getter/setter must INVOKE the accessor (a static
					// `get_/set_<name>` method on the file class, like the extension-property path below but without a
					// receiver), NOT read/write the raw static field. bir2cir binds the `prop:get`/`prop:set` marker to
					// the `get_`/`set_` method by convention. Read/write customness is independent (a `var` may pair a
					// custom setter with a default getter, or vice versa); a default accessor stays a raw field access.
					val (customGet, customSet) = callableId?.let { kotc.frontend.clrInjectedTopLevelPropCustomAccessor(it) } ?: (false to false)
					if (callee === p.setter) {
						return if (customSet)
							"""{"k":"callStatic","ownerType":${fqnJson(fileClass)},"method":${str(p.name.asString())},"prop":"set","argTypes":[${birType(regularArgs(call).first().type).toJson()}],"ret":${fqnJson("kotlin.Unit")},"args":[${expr(regularArgs(call).first())}]}"""
						else """{"k":"staticFieldSet","ownerType":${fqnJson(fileClass)},"name":${str(p.name.asString())},"value":${expr(regularArgs(call).first())}}"""
					}
					return if (customGet)
						"""{"k":"callStatic","ownerType":${fqnJson(fileClass)},"method":${str(p.name.asString())},"prop":"get","argTypes":[],"ret":${birType(callee.returnType).toJson()},"args":[]}"""
					else """{"k":"staticField","ownerType":${fqnJson(fileClass)},"name":${str(p.name.asString())}}"""
				}
				val recv = extensionReceiver(call)
				// A2 (#61 / step 3): a top-level EXTENSION property accessor is a static `get_/set_<name>(__self)` METHOD
				// on the referenced file class (NOT a .NET property) -> emit the plain static call by identity carrying
				// the accessor KIND; bir2cir's NetInteropBinding finds no matching .NET property/field and applies the
				// `get_`/`set_` convention -> a clrStatic method call.
				if (callee === p.setter) {
					val args = listOfNotNull(recv) + regularArgs(call)
					return """{"k":"callStatic","ownerType":${fqnJson(fileClass)},"method":${str(p.name.asString())},"prop":"set","argTypes":[${args.joinToString(",") { birType(it.type).toJson() }}],"ret":${fqnJson("kotlin.Unit")},"args":[${args.joinToString(",") { expr(it) }}]}"""
				}
				return """{"k":"callStatic","ownerType":${fqnJson(fileClass)},"method":${str(p.name.asString())},"prop":"get","argTypes":[${recv?.let { birType(it.type).toJson() } ?: ""}],"ret":${birType(callee.returnType).toJson()},"args":[${recv?.let { expr(it) } ?: ""}]}"""
			}
		}

	// Top-level property (parent is the file/package, not a class) -> a static field of ITS DEFINING file's
	// class. Use the property's own file, NOT the file currently being emitted — else a cross-file reference
	// looks for `<ReferencingFile>Kt.prop` and fails (`field XKt.prop not found`; feedback item 11).
	(callee.correspondingPropertySymbol?.owner)?.let { p ->
		if (declaringClass == null) {
			// A TOP-LEVEL delegated property (`val x by Provider()`): its storage is a STATIC `x$delegate` field on the
			// file class; the access routes to the delegate's getValue/setValue with a NULL thisRef (no enclosing
			// instance) + a materialized KProperty. Mirrors the member delegated path (declaringClass != null) with a
			// static delegate field and a `null` thisRef. bir2cir/ilemit resolve the real getValue/setValue — no CLR
			// knowledge here. A plain top-level property (no delegate) falls through to the static-field/accessor path.
			if (p.isDelegated) {
				val bf = p.backingField
				val fileClass = fileClassOf(p)
				val delegate = bf?.let { """{"k":"staticField","ownerType":${fqnJson(fileClass)},"name":${str(it.name.asString())}}""" }
				// The delegate convention's `thisRef` (getValue/setValue's 1st arg): a plain top-level property has NO
				// enclosing instance -> a `null` Any? const; a top-level EXTENSION delegated property passes its extension
				// RECEIVER as thisRef (never silently dropped — that would run getValue with the wrong receiver).
				val thisRef = extensionReceiver(call)?.let { expr(it) } ?: """{"k":"const","type":${OBJ.toJson()},"value":null}"""
				// `by lazy` (top-level): a real kotlin.Lazy<T> -> read its `value` getter, dropping thisRef/KProperty
				// (mirrors the member `by lazy` inline).
				if (callee === p.getter && delegate != null && bf?.type?.classFqName?.asString() == "kotlin.Lazy") {
					val owner = ownerSpec(bf.type.classifierOrNull?.owner as? IrClass, bf.type)
					return """{"k":"callInstance","ownerType":${owner.toJson()},"virtual":true,"recv":$delegate,"method":"get_value","args":[]${retHint((owner as? TypeNode.Fqn)?.args != null, callee.returnType)}}"""
				}
				val delegateClass = bf?.type?.classifierOrNull?.owner as? IrClass
				val isUserDelegate = delegateClass != null && !isExternalNetType(delegateClass) &&
					delegateClass.fqNameWhenAvailable?.asString()?.startsWith("kotlin") != true
				val bfFq = bf?.type?.classFqName?.asString()
				val (owner, ownerGeneric) = when {
					isUserDelegate -> fqnJson(typeName(delegateClass!!)) to false
					bf != null && (bfFq == "kotlin.properties.ReadWriteProperty" || bfFq == "kotlin.properties.ReadOnlyProperty") -> {
						val os = ownerSpec(bf.type.classifierOrNull?.owner as? IrClass, bf.type)
						os.toJson() to ((os as? TypeNode.Fqn)?.args != null)
					}
					else -> null to false
				}
				if (delegate != null && owner != null) {
					val kprop = kPropertyStub(p.name.asString())
					return if (callee === p.setter)
						"""{"k":"callInstance","ownerType":$owner,"virtual":true,"recv":$delegate,"method":"setValue","args":[$thisRef,$kprop,${expr(regularArgs(call).first())}]}"""
					else """{"k":"callInstance","ownerType":$owner,"virtual":true,"recv":$delegate,"method":"getValue","args":[$thisRef,$kprop]${retHint(ownerGeneric, callee.returnType)}}"""
				}
				// `val x by map` (top-level, extension-convention delegate): FIR resolved the accessor to the stdlib
				// getValue/setValue extension — re-emit it as the owner-null static call the general top-level-extension
				// path produces (thisRef null, receiver-first args + typeArgs). Mirrors the member Map fallthrough.
				run {
					val accessor = callee as? IrSimpleFunction ?: return@run
					val stmts = (accessor.body as? IrBlockBody)?.statements ?: return@run
					val bodyCall = stmts.mapNotNull { st -> (st as? IrReturn)?.value as? IrCall ?: st as? IrCall }.singleOrNull() ?: return@run
					val target = bodyCall.symbol.owner
					if (delegate == null || target.parent is IrClass) return@run
					if (target.name.asString() != "getValue" && target.name.asString() != "setValue") return@run
					val kprop = kPropertyStub(p.name.asString())
					val ta = typeArgsJson(bodyCall)
					val setArg = if (callee === p.setter) ",${expr(regularArgs(call).first())}" else ""
					return """{"k":"callStatic","owner":null,"method":${str(target.name.asString())}${overloadSigField(target)}$ta${retHintStr(ta.isNotEmpty(), birType(callee.returnType))},"args":[$delegate,$thisRef,$kprop$setArg]${calleeOwnerTag(target)}}"""
				}
				return unsupported(call, "this top-level delegated property",
					"its delegate type could not be resolved to a supported form (lazy, a custom getValue/setValue, or a Map)")
			}
			val ext = extensionReceiver(call)
			// C7: a TOP-LEVEL EXTENSION property (`val List<T>.lastIndex`, `val Int.absoluteValue`, `val
			// CharSequence.indices`) has NO real static field — its value is an accessor emitted by the property's
			// OWN bare Kotlin identity + a `"prop":"get"/"set"` marker (#78/#81) whose leading arg is the extension
			// receiver. Emit it `owner=null`, so bir2cir attributes it to the ref.dll file class in a cross-module app
			// build (the owner-null top-level substitution axis — UNTOUCHED). It ALSO carries `calleeOwner` (#199 Design
			// B, same two-axis contract as a top-level FUNCTION call): a same-module same-simple-name extension property
			// across two packages disambiguates by the FIR-resolved file-class DISPATCH hint at ilemit, without shadowing
			// substitution. bir2cir shapes the .NET accessor from the stdlib binding metadata, falling back to
			// kotc's get_/set_<name> declaration convention when none exists. `sig` disambiguates a same-name overload
			// by receiver type. A cross-module DESERIALIZED stub can spuriously report a backing field, so an
			// extension property must NEVER fall to the static-field read below — that dropped the receiver and looked
			// up `<CurrentFileKt>.<name>` as a field (the C7 `field AppKt.lastIndex not found` crash).
			if (ext != null) {
				// A GENERIC extension property (`val List<T>.lastIndex`/`.indices`) has a generic <name>[T] accessor —
				// carry the resolved type args (+ a retType hint) so ilemit MakeGenericMethods it; without them the call
				// hits the uninstantiated generic method ("type is not fully instantiated"). Mirrors the generic
				// extension-FUNCTION path. A non-generic getter (Int.absoluteValue, CharSequence.lastIndex) emits no ta.
				val ta = typeArgsJson(call)
				return if (callee === p.setter) {
					val args = listOf(ext) + regularArgs(call)
					"""{"k":"callStatic","owner":null,"method":${str(p.name.asString())},"prop":"set"${overloadSigField(callee)}$ta,"args":[${args.joinToString(",") { expr(it) }}]${calleeOwnerTag(p)}}"""
				} else
					"""{"k":"callStatic","owner":null,"method":${str(p.name.asString())},"prop":"get"${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty(), birType(call.type))},"args":[${expr(ext)}]${calleeOwnerTag(p)}}"""
			}
			// A plain top-level property (parent is the file/package, not a class) -> a static field of ITS DEFINING
			// file's class. Use the property's own file, NOT the file currently being emitted — else a cross-file
			// reference looks for `<ReferencingFile>Kt.prop` and fails (`field XKt.prop not found`; feedback item 11).
			// #89: fileClassOf returns the DECLARING file class only when the property is SAME-MODULE (its parent is a
			// real IrFile). A CROSS-MODULE property is a lazy declaration deserialized from a dependency (the frontend
			// metadata klib, which is PACKAGE-keyed — the file grouping survives ONLY in the ref.dll bir2cir reads), so
			// its parent is a package fragment, NOT an IrFile, and fileClassOf falls back to the READING file's class —
			// mis-attributing e.g. a cross-module `COROUTINE_SUSPENDED` read to `<ReaderFile>Kt` (the #80 root that
			// forced a bir2cir owner-rebind band-aid). kotc genuinely CANNOT name the declaring file class here (it is
			// CLR/ref knowledge), so for the ACCESSOR (`prop:get`/`prop:set`) emission it declares the owner UNRESOLVED
			// (`owner:null`) — the SAME honest fact it emits for a cross-module top-level FUNCTION — and bir2cir binds
			// the true declaring file class off the ref.dll (its owner-null top-level resolver), no wrong-owner rebind.
			// (A raw cross-module static FIELD read cannot be owner-null-resolved and has no reachable case — every such
			// top-level val is a computed accessor — so the staticField branches keep the fileClassOf owner.)
			val crossModule = p.parent !is IrFile
			val owner = fileClassOf(p)
			val accessorOwner = if (crossModule) "null" else fqnJson(owner)
			// A COMPUTED top-level property (`val foo: T get() = ...`, no backing field) OR one that has a backing
			// field (initializer) but ALSO a CUSTOM accessor (`val foo = 41; get() = field + 1`, #89) -> a static
			// call by the property's OWN bare Kotlin identity + a `"prop":"get"/"set"` marker (#78/#81), NOT the
			// baked get_/set_ slot name and NOT a raw static-field load (that would skip the custom accessor).
			// bir2cir shapes the .NET accessor from the stdlib binding metadata, falling back to kotc's
			// get_/set_<name> declaration convention when none exists. The read/write decisions are independent: a
			// `var` may pair a default getter (field read) with a custom setter (accessor call), or vice versa.
			return if (callee === p.setter) {
				if (!writesAsStaticField(p))
					"""{"k":"callStatic","owner":$accessorOwner,"method":${str(p.name.asString())},"prop":"set","args":[${regularArgs(call).joinToString(",") { expr(it) }}]}"""
				else
					"""{"k":"staticFieldSet","ownerType":${fqnJson(owner)},"name":${str(p.name.asString())},"value":${expr(regularArgs(call).first())}}"""
			} else {
				if (!readsAsStaticField(p))
					"""{"k":"callStatic","owner":$accessorOwner,"method":${str(p.name.asString())},"prop":"get","args":[]${retHint(false, call.type)}}"""
				else
					"""{"k":"staticField","ownerType":${fqnJson(owner)},"name":${str(p.name.asString())}}"""
			}
		}
	}

	// `s.length` on a String is NOT intercepted here: it's a real `kotlin.String.length` property read — fall
	// through to the ordinary property-get path so it emits as a `kotlin.String` `get_length` member call. The
	// CLR binding (String.length -> System.String.Length) is stdlib `@ClrIntrinsic("Length")` metadata, applied
	// by bir2cir's MemberCallSubstitution (the sibling `String.get`->`get_Chars` was cleaned the same way). kotc
	// carries NO CLR knowledge here (layer boundary — CLAUDE.md §"kotc reads NEITHER @ClrIntrinsic…").
	// Pair/Triple `.first`/`.second`/`.third` and IndexedValue `.index`/`.value` are NOT intercepted: they are real
	// `kotlin.Pair`/`kotlin.Triple`/`kotlin.collections.IndexedValue` property reads — fall through to the ordinary
	// member-property-read path so they emit as `get_first`/`get_index`/... accessor calls. Their stdlib backing
	// fields are accessor-routed (internal), so a raw cross-assembly field read never binds directly; the faithful
	// property call is what ilemit already resolves (its external-owner field node re-routes to the getter anyway).

	// Property get/set on a user class -> field access.
	val property = callee.correspondingPropertySymbol?.owner
	// `.size` -> CIL array length (arrays) or `Enumerable.Count` (collections).
	if (property?.name?.asString() == "size") dispatchReceiver(call)?.let { r ->
		if (isArrayType(r.type)) return """{"k":"arrayLen","array":${expr(r)}}"""
		// `Color.entries.size`: entries -> a Color[] (enumValues), so .size is the array length.
		if (r.type.classFqName?.asString() == "kotlin.enums.EnumEntries") return """{"k":"arrayLen","array":${expr(r)}}"""
		// kotlin.* collection/map `.size` is NOT intercepted: it's a real `size` property — fall through to the
		// ordinary property read so it emits as a kotlin.* `get_size` call.
	}
	// `kProperty.name` is NOT intercepted here (#70): `kotlin.reflect.KProperty*`/`KCallable.name` is a REAL
	// emitted stdlib interface member now (kotc's `propertyRef`/`kPropertyStub` materialize real implementations
	// of it) — it falls through to the ordinary member-property-read path below, emitting the SAME
	// `callInstance ownerType:kotlin.reflect.KProperty(/KCallable) method:get_name` shape this used to hand-roll,
	// just with the real FQN instead of the retired `dotkt$KProperty` synthetic.
	// Delegated property access. `by lazy`: `obj.x` -> `obj.x$delegate.value` (a plain `kotlin.Lazy<T>::get_value`
	// read; see the lazy case below), dropping thisRef/KProperty. Custom (duck-typed) delegate: route to its
	// getValue/setValue, passing thisRef and a materialized `KProperty` (compiler-generated). Stdlib-interface
	// delegates -> deferred.
	if (property != null && property.isDelegated && declaringClass != null) {
		val bf = property.backingField
		val recv = dispatchReceiver(call)?.let { expr(it) } ?: """{"k":"this"}"""
		val delegate = bf?.let { """{"k":"field","ownerType":${fqnJson(typeName(declaringClass))},"recv":$recv,"name":${str(it.name.asString())}}""" }
		// `by lazy` (member): the delegate is a real `kotlin.Lazy<T>` (the stdlib `UnsafeLazyImpl`). Its accessor is
		// the InlineOnly `Lazy<T>.getValue(…) = value` operator, whose stdlib inline body is absent from our IR;
		// inline it (a pure Kotlin-frontend fact) to a plain read of the Lazy interface's `value` getter. bir2cir/
		// ilemit resolve the real emitted `kotlin.Lazy::get_value` — no CLR (System.Lazy) knowledge in kotc.
		if (callee === property.getter && bf?.type?.classFqName?.asString() == "kotlin.Lazy") {
			val owner = ownerSpec(bf.type.classifierOrNull?.owner as? IrClass, bf.type)
			return """{"k":"callInstance","ownerType":${owner.toJson()},"virtual":true,"recv":$delegate,"method":"get_value","args":[]${retHint((owner as? TypeNode.Fqn)?.args != null, callee.returnType)}}"""
		}
		// `val x by map` is NOT intercepted: FIR routes it through the stdlib `Map.getValue`/`setValue` operator —
		// fall through to the getValue/setValue delegate routing so it emits as real kotlin.* calls.
		// Route getValue/setValue to the delegate object. The dispatch type is either the concrete user
		// delegate class (duck-typed or implementing Read(Write)Property) or — when the field is typed as
		// the Read(Write)Property interface (e.g. `by Delegates.observable(…)`, `by Delegates.notNull()`) —
		// the REAL generic stdlib `kotlin.properties.Read(Write)Property<T,V>` interface. That mirrors the
		// `by lazy` path (dispatch on the real generic `kotlin.Lazy<T>`): the delegate value is the real
		// emitted stdlib `ObservableProperty`/`NotNullVar`, which implements the real generic interface, so
		// the call binds to the actual stdlib getValue/setValue — no compiler-synthesized delegate class.
		val delegateClass = bf?.type?.classifierOrNull?.owner as? IrClass
		val isUserDelegate = delegateClass != null && !isExternalNetType(delegateClass) &&
			delegateClass.fqNameWhenAvailable?.asString()?.startsWith("kotlin") != true
		val bfFq = bf?.type?.classFqName?.asString()
		val (owner, ownerGeneric) = when {
			isUserDelegate -> fqnJson(typeName(delegateClass!!)) to false
			bf != null && (bfFq == "kotlin.properties.ReadWriteProperty" || bfFq == "kotlin.properties.ReadOnlyProperty") -> {
				val os = ownerSpec(bf.type.classifierOrNull?.owner as? IrClass, bf.type)
				os.toJson() to ((os as? TypeNode.Fqn)?.args != null)
			}
			else -> null to false
		}
		if (delegate != null && owner != null) {
			val kprop = kPropertyStub(property.name.asString())
			// callvirt: getValue/setValue is virtual (interface impl) or final (duck-typed) — callvirt fits both.
			return if (callee === property.setter)
				"""{"k":"callInstance","ownerType":$owner,"virtual":true,"recv":$delegate,"method":"setValue","args":[$recv,$kprop,${expr(regularArgs(call).first())}]}"""
			else """{"k":"callInstance","ownerType":$owner,"virtual":true,"recv":$delegate,"method":"getValue","args":[$recv,$kprop]${retHint(ownerGeneric, callee.returnType)}}"""
		}
		// `val x by map` (a TOP-LEVEL-extension delegate convention): FIR resolved the accessor to the stdlib
		// `kotlin.collections.getValue/setValue(thisRef, property)` extension (MapAccessors.kt) — the resolved
		// symbol sits in the accessor's own generated body. Re-emit it at the access site as the plain owner-null
		// static call the general top-level-extension path produces (receiver-first args + declared sig +
		// typeArgs), so bir2cir/ilemit resolve the real rt-stdlib method like any other cross-module stdlib call.
		// (Pure Kotlin: the target comes from FIR resolution, no CLR knowledge here.)
		run {
			val accessor = callee as? IrSimpleFunction ?: return@run
			val stmts = (accessor.body as? IrBlockBody)?.statements ?: return@run
			val bodyCall = stmts.mapNotNull { st -> (st as? IrReturn)?.value as? IrCall ?: st as? IrCall }.singleOrNull() ?: return@run
			val target = bodyCall.symbol.owner
			if (delegate == null || target.parent is IrClass) return@run
			if (target.name.asString() != "getValue" && target.name.asString() != "setValue") return@run
			val kprop = kPropertyStub(property.name.asString())
			val ta = typeArgsJson(bodyCall)
			val setArg = if (callee === property.setter) ",${expr(regularArgs(call).first())}" else ""
			return """{"k":"callStatic","owner":null,"method":${str(target.name.asString())}${overloadSigField(target)}$ta${retHintStr(ta.isNotEmpty(), birType(callee.returnType))},"args":[$delegate,$recv,$kprop$setArg]${calleeOwnerTag(target)}}"""
		}
		return unsupported(call, "this delegated property",
			"its delegate type could not be resolved to a supported form (lazy, a custom getValue/setValue, or a Map)")
	}
	if (property != null && declaringClass != null) {
		val recvExpr = dispatchReceiver(call)
		val recv = recvExpr?.let { expr(it) } ?: """{"k":"this"}"""
		val ownerStr = ownerSpec(declaringClass, recvExpr?.type)
		val owner = str(ownerStr)
		// A property with a custom accessor — OR one overriding an interface property (e.g. CharSequence.length) —
		// routes through the get_/set_ method, not the backing field. The Kotlin<->CLR slot-name binding (get_length
		// -> the synthetic dotkt_CharSequence slot / a @ClrIntrinsic member) is bir2cir's, off the `overrides` marker.
		if (!property.isLateinit && !isClrField(property)) {   // route through get_/set_ accessor (CLR property model); @ClrField reads/writes the plain field
			val virtual = isVirtualInstanceCall(call, callee)
			// A MEMBER extension property (`class C { val T.p get() }`): dispatch on the enclosing C, but its `get_p`/
			// `set_p` method takes the extension receiver as a leading `__self` arg -> prepend it.
			val pExt = extensionReceiver(call)?.let { expr(it) }
			return if (callee === property.setter)
				"""{"k":"callInstance","ownerType":$owner,"virtual":$virtual,"recv":$recv,"method":${str("set_" + property.name.asString())},"args":[${listOfNotNull(pExt, expr(regularArgs(call).first())).joinToString(",")}]${overridesJson(callee)}${superTag(call)}}"""
			else """{"k":"callInstance","ownerType":$owner,"virtual":$virtual,"recv":$recv,"method":${str("get_" + property.name.asString())},"args":[${pExt ?: ""}]${retHint((ownerStr as? TypeNode.Fqn)?.args != null, call.type)}${overridesJson(callee)}${superTag(call)}}"""
		}
		return if (callee === property.setter)
			"""{"k":"setFieldExpr","ownerType":$owner,"recv":$recv,"name":${str(property.name.asString())},"value":${expr(regularArgs(call).first())}}"""
		// `lateinit var` read -> throw if still uninitialized (the field is null) — proper lateinit semantics.
		else if (property.isLateinit)
			"""{"k":"lateinitGet","ownerType":$owner,"recv":$recv,"name":${str(property.name.asString())}}"""
		else """{"k":"field","ownerType":$owner,"recv":$recv,"name":${str(property.name.asString())}${retHint((ownerStr as? TypeNode.Fqn)?.args != null, call.type)}}"""
	}

	// Kotlin universal methods (hashCode/toString/equals) on a builtin receiver. The System.Object slot is correct
	// ONLY for a GENUINE universal call — one whose receiver TYPE does not declare its OWN routable override:
	//  - the resolved callee is the inherited kotlin.Any member (a fake override): Int/Long/Char/Boolean.hashCode,
	//    or a bare List/Set/Map.toString (emitted as objMethod ToString with a `recvType` hint; bir2cir routes it
	//    Kotlin-style), or Any/generic; and
	//  - a PRIMITIVE value type's toString/equals — those are declared but bodyless (no Kotlin body to hoist, no
	//    @ClrIntrinsic), so bir2cir has nothing to route to and the BCL value type's ToString/Equals IS correct;
	//    and Int/Long/Char/Boolean/Float/Double's hashCode, which the CLR stdlib does NOT declare (it inherits the
	//    kotlin.Any slot), so it stays objMethod → the BCL value type's GetHashCode (#167/#168).
	// When the receiver TYPE declares its OWN routable override — String's @ClrIntrinsic hashCode/toString/equals,
	// a Pair|Triple|data-class toString (→ C11) — the call must REACH that member, so FALL THROUGH to the ordinary
	// member-call path (bir2cir routes it: a real body → rule-3 helper, an @ClrIntrinsic → its BCL slot). Routing a
	// declared override to System.Object here shadows the correct Kotlin body — the C11 miscompiles.
	if (isBuiltin && dispatchReceiver(call) != null) {
		// The receiver TYPE declares its OWN override iff the resolved callee is a real (non-fake-override) member of a
		// type OTHER than kotlin.Any. A call resolved DIRECTLY to `kotlin.Any.hashCode/toString/equals` — e.g.
		// `element.toString()` on a generic `T` with no more-derived override — is NOT a fake override yet IS the
		// universal method, so it must keep the System.Object slot (falling through would emit a call to the
		// non-existent `kotlin.Any.toString` and NRE). Hence the explicit kotlin.Any exclusion beside isFakeOverride.
		val declaresOwn = !callee.isFakeOverride && declaringClass?.fqNameWhenAvailable?.asString() != "kotlin.Any"
		val primitive = dispatchReceiver(call)!!.type.isPrimitiveOrUnsigned()
		// A `super.toString()`/`super.hashCode()`/`super.equals()` (issue #14) resolving to the kotlin.Any slot must NOT
		// become an `objMethod` — that is UNCONDITIONALLY a `callvirt object::…` in ilemit, which re-dispatches by the
		// receiver's runtime type back to THIS class's override and infinite-loops. Fall through to the ordinary
		// member-call path, which emits a NON-virtual `callInstance` (isVirtualInstanceCall → virtual:false) carrying
		// `anySlot:true`; bir2cir renames the slot + resolves the kotlin.Any owner to System.Object, ilemit's `call`
		// reaches the base slot exactly like C#'s `base.ToString()`. The receiver of a super call is always `this` (a
		// reference class), never a primitive, so this never disturbs the value-type objMethod routing.
		val isSuper = call.superQualifierSymbol != null
		val fallThrough = isSuper || when (name) {
			"hashCode" -> declaresOwn                      // Int/Long/Char/Boolean/Float/Double inherit Any.hashCode → stays objMethod (String's @ClrIntrinsic hashCode falls through)
			"toString", "equals" -> declaresOwn && !primitive
			else -> false
		}
		if (!fallThrough) when (name) {
			"hashCode" -> return """{"k":"objMethod","method":"hashCode","recv":${expr(dispatchReceiver(call)!!)}}"""
			"toString" -> if (regularArgs(call).isEmpty()) {
				// Emit the FAITHFUL objMethod toString. bir2cir recovers the receiver's static type via StaticType (no
				// kotc hint) and, for a collection/Map receiver, routes to the Kotlin-style clrCollToString /
				// clrMapToString helper (`[a, b]` / `{a=1, b=2}`); else it renames to the .NET ToString slot.
				val recvE = dispatchReceiver(call)!!
				return """{"k":"objMethod","method":"toString","recv":${expr(recvE)}}"""
			}
			"equals" -> {
				val recvE = dispatchReceiver(call)!!; val argE = regularArgs(call).first()
				// Emit the FAITHFUL objMethod equals. An EXPLICIT `.equals()` on a boxed Double/Float / a collection
				// follows Kotlin's TOTAL order / STRUCTURAL equality (Object.Equals gives IEEE
				// `(-0.0).equals(0.0)==true` / reference identity), so bir2cir recovers the receiver/arg static types
				// via StaticType (no kotc hint) and routes to the SAME helper the EQEQ path uses; else it keeps
				// Object.Equals.
				return """{"k":"objMethod","method":"equals","recv":${expr(recvE)},"arg":${expr(argE)}}"""
			}
		}
	}
	// `n.toString(radix)` is NOT lowered in kotc (C4, 2026-07-06). The former `System.Convert.ToString(value, base)`
	// special-case was BOTH a layer violation (a BCL name in kotc) AND wrong: Convert.ToString renders a negative in
	// two's-complement (`(-255).toString(16)` -> "ffffff01", not "-ff") and THROWS for a base outside {2,8,10,16}
	// (`35.toString(36)` -> ArgumentException "Invalid Base"). The stdlib actual (StringNumberConversionsClr.kt) has
	// the correct sign-and-arbitrary-digit body; kotc now emits the plain `kotlin.text` Int/Long.toString(radix)
	// extension call and bir2cir attributes it to StringNumberConversionsKt so the real body runs.

	if (isBuiltin) {
		val operands = call.arguments.filterNotNull()
		// `String + x` (concatenation, not numeric add) is NOT recognized here: kotc emits the plain
		// `callInstance kotlin.String.plus` (a faithful member call) via the general member-call path, and bir2cir's
		// PrimitiveOperatorLowering re-emits the `concat` (recovering each part's static type via StaticType, applying
		// the collection/nullable part routing) — the `String.plus -> concat` MEMBER recognition is bir2cir's.
		// `==` (EQEQ) / `===` (EQEQEQ) are `kotlin.internal.ir` COMPILER INTRINSICS. ALL of the ceq-vs-Object.Equals
		// SPLIT + the Kotlin-SEMANTIC structural routings (collection `==`, boxed Double/Float total-order `==`)
		// recognition lives in bir2cir: kotc emits ONLY the FAITHFUL intrinsic call with owner =
		// `kotlin.internal.ir` (collision-safe). PrimitiveOperatorLowering recovers the operands' SURFACE static type
		// (prim fast-path -> ceq) and VALUE static type (collection/float helpers, else objEq) via StaticType; no
		// argTypes/argValueTypes hints are emitted — the operand expression nodes + the local env carry the types.
		if (name == "EQEQ" && operands.size == 2)
			return """{"k":"callStatic","owner":${fqnJson("kotlin.internal.ir")},"method":"EQEQ","args":[${expr(operands[0])},${expr(operands[1])}]}"""
		if (name == "EQEQEQ" && operands.size == 2)
			return """{"k":"callStatic","owner":${fqnJson("kotlin.internal.ir")},"method":"EQEQEQ","args":[${expr(operands[0])},${expr(operands[1])}]}"""
		// The IR comparison intrinsics (`kotlin.internal.ir.less`/`lessOrEqual`/`greater`/`greaterOrEqual` — the
		// `<`/`<=`/`>`/`>=` desugarings, top-level with plain value params). Recognition + operand shaping is
		// bir2cir's: kotc emits ONLY the FAITHFUL intrinsic call with owner = its home package `kotlin.internal.ir`
		// (collision-safe — a user top-level `less` is NOT `isBuiltin` and never has this owner), args = the plain
		// operand expressions. bir2cir's PrimitiveOperatorLowering re-emits `{k:binOp, op:<}` and does the operand
		// shaping (primitive gating, nullable-primitive `Nullable<T>.Value` unwrap, boxed-Any -> concrete cast) via
		// StaticType — exactly like EQEQ/EQEQEQ above. The Kotlin<->CLR relation lives there, not in kotc.
		if (name in setOf("less", "lessOrEqual", "greater", "greaterOrEqual") && operands.size == 2
				&& callee.parameters.none { it.kind == IrParameterKind.ExtensionReceiver })
			return """{"k":"callStatic","owner":${fqnJson("kotlin.internal.ir")},"method":${str(name)},"args":[${expr(operands[0])},${expr(operands[1])}]}"""
		// UNARY (unaryMinus/unaryPlus/not/inv) recognition is bir2cir's: kotc emits the faithful
		// `callInstance kotlin.Int.unaryMinus()` (0-arg member) and bir2cir re-emits `{k:unaryOp}` from the
		// PRIMITIVE_OP_FQ owner. The receiver is value-shaped by the general callInstance path (recvExpr).
		// `i.inc()`/`i.dec()` (the `i++`/`i--` desugaring) recognition is bir2cir's: kotc emits
		// the faithful `callInstance kotlin.Int.inc()` (0-arg member, receiver value-shaped by recvExpr) and
		// PrimitiveOperatorLowering re-emits `(recv + 1)`/`(recv - 1)` (the `const 1:kotlin.Int` literal moves there).
		// Numeric conversion `x.toLong()`/`x.toInt()`/… is not recognized here: kotc emits the plain
		// `callInstance kotlin.Int.toLong` (the faithful IR); bir2cir reads the `@kotlin.clr.ClrConv` marker off the
		// stdlib primitive's conversion member on the ref.dll and emits the `conv` node from the callee's return type.
		// `println(...)`/`print(...)` are NOT recognized here: kotc emits the plain top-level `callStatic owner:null`
		// via the general call path, and bir2cir substitutes it to System.Console.Write/WriteLine off the stdlib
		// @ClrIntrinsic (runtime/stdlib/clr/kotlin/io/ConsoleClr.kt) and wraps a collection/Map arg in
		// clrCollToString/clrMapToString (Kotlin-style `[a, b]`) — recovering the operand static types via StaticType.
		// `readLine()` is NOT lowered: the CLR stdlib exposes readln()/readlnOrNull() (readlnOrNull is @ClrIntrinsic-bound
		// to System.Console.ReadLine in ConsoleClr.kt). There is no `kotlin.io.readLine` symbol in the frontend KLIB.
		// Regex is NOT lowered here: `kotlin.text.Regex` is
		// @ClrTypeAlias("System.Text.RegularExpressions.Regex") with `containsMatchIn`@ClrIntrinsic("IsMatch") /
		// `replace`@ClrIntrinsic("Replace") + real Kotlin bodies for `matches`/`find`/`split`/`.value`
		// (runtime/stdlib/clr/kotlin/text/regex/RegexClr.kt). kotc emits `"p".toRegex()` as a plain call to the stdlib
		// `String.toRegex()` extension (= `Regex(this)`) and `r.containsMatchIn(s)`/`r.replace(...)` as plain member
		// calls on kotlin.text.Regex; bir2cir substitutes the @ClrTypeAlias ctor + @ClrIntrinsic members off the
		// ref.dll and runs the real bodies. The Kotlin<->CLR relation lives in bir2cir, not kotc.
		// `String.format` is NOT lowered here. System.String.Format would be CLR knowledge in kotc, and it is
		// dead against the frontend KLIB anyway — that jar has no `kotlin.text.String.Companion.format`, so the
		// symbol is unresolved before the backend ever runs. Making `String.format` work is a stdlib concern (bind a
		// `String.Companion.format(String, vararg Any?)` @ClrIntrinsic("System.String.Format")), NOT a kotc lowering.
		// `noWhenBranchMatchedException` / `throwUninitializedPropertyAccessException` are COMPILER INTRINSICS (the
		// exhaustive-when synthetic-else / uninitialized-property-access throws), siblings of ieee754equals/EQEQ/... —
		// kotc emits ONLY the FAITHFUL intrinsic call with owner = the callee's real resolved parent FQN
		// (collision-safe). bir2cir re-emits the throw (Kotlin IllegalStateException, substituted to the BCL type via
		// the ref.dll @ClrTypeAlias). The recognition + throw synthesis is bir2cir's, not kotc's.
		// NOTE: on THIS (CLR) pipeline only `noWhenBranchMatchedException` actually reaches here (top-level, owner
		// `kotlin.internal.ir`); a `lateinit` access lowers to a dedicated `lateinitGet` node earlier, so
		// `throwUninitializedPropertyAccessException` is never produced — its name-branch is defensive.
		if (name == "noWhenBranchMatchedException" || name == "throwUninitializedPropertyAccessException") {
			// FAITHFUL owner = the callee's real resolved parent FQN (the home package for the top-level intrinsic;
			// the enclosing class if a member-form callee ever appears). The final literal is an unreachable
			// last-resort default, not a preferred guess — the resolved FQN always wins ahead of it.
			val intrinsicOwner = declaringClass?.fqNameWhenAvailable?.asString()
				?: pkgFqName
				?: callee.fqNameWhenAvailable?.asString()?.substringBeforeLast('.', "")?.takeIf { it.isNotEmpty() }
				?: "kotlin.internal.ir"
			return """{"k":"callStatic","owner":${fqnJson(intrinsicOwner)},"method":${str(name)},"args":[${regularArgs(call).joinToString(",") { expr(it) }}]}"""
		}
		// `ieee754equals` is a `kotlin.internal.ir` COMPILER INTRINSIC, a sibling of EQEQ/EQEQEQ/less/... — kotc
		// emits ONLY the FAITHFUL intrinsic call with owner = `kotlin.internal.ir` (collision-safe); bir2cir's
		// PrimitiveOperatorLowering re-emits the `binOp ==` (the ordered IEEE-754 comparison). The Kotlin<->CLR
		// relation lives there, not in kotc.
		if (name == "ieee754equals" && regularArgs(call).size == 2) {
			val a = regularArgs(call)
			return """{"k":"callStatic","owner":${fqnJson("kotlin.internal.ir")},"method":"ieee754equals","args":[${expr(a[0])},${expr(a[1])}]}"""
		}
		// The top-level precondition / error helpers (`kotlin.TODO`/`error`/`require`/`check`/`requireNotNull`/
		// `checkNotNull`) are NOT special-cased here. The no-lambda overloads fall through to the general top-level
		// call path (`callStatic owner:null method:<name> args:[...]`); bir2cir recognizes them by callee name and
		// synthesizes the throw / condition. The lambda-taking overloads (`require(c){msg}` etc.) route through the
		// owner-less `callInline` node below (AXIS ①: any lambda arg splices), and bir2cir splices the real body.
		// Either way the Kotlin-semantic lowering lives in bir2cir, not kotc.
		// `coerceAtMost`/`coerceAtLeast`/`coerceIn` are NOT lowered here (layer purity).
		// System.Math.Min/Max/Clamp would be a BCL name in kotc (a layer violation). The stdlib
		// `_Ranges.kt` funcs are pure Kotlin with correct bodies (`if (this < min) min else this`), so kotc now emits a
		// plain call and the real stdlib body runs. This is also MORE correct than Math.Min for floats: Kotlin's coerce
		// uses `<`/`>` (total-ordering / NaN-propagating) semantics that differ from System.Math.Min/Max on NaN.
		// (No @ClrIntrinsic needed: the pure body IS the binding — the top-preferred "emit the real body" outcome.)
		// `repeat(n) { i -> body }` is NOT special-cased here (#75): a LITERAL lambda (AXIS ①) rides the general
		// owner-less `callInline` gate (bir2cir splices `kotlin.repeat` off the ref.dll and wraps the counted loop);
		// a callable-ref / non-lambda action falls through to the plain top-level call, which bir2cir's
		// RepeatInlineLowering re-emits as a delegate counter loop.
		// `kotlin.math.*` is NOT lowered here. kotc emits a plain call to the stdlib fun (owner=null callStatic /
		// an extension instance for Double.pow); bir2cir's MemberCallSubstitution reads MathClr.kt's @ClrIntrinsic
		// bindings off the ref.dll and substitutes System.Math.* / System.MathF.* — the CLR relation lives there, not
		// in kotc.
		// `kotlin.text` String ops are NOT name-lowered in kotc: kotc emits a plain call; bir2cir attributes it to
		// StringsKt and the StringCharSequenceBridge (run on the RT stdlib build too) coerces the String receiver/args
		// into the `dotkt$CharSequence` adapter so the CharSequence-extension body runs (contains/indexOf/startsWith/
		// endsWith/split/substring/isEmpty/isNotEmpty/uppercase/lowercase/isBlank/reversed/etc.). `reversed` is a plain
		// call too: the real stdlib `CharSequence.reversed() = StringBuilder(this).reverse()` runs — bir2cir's TransformNew
		// coerces the CharSequence ctor arg to String so `StringBuilder(String)` binds. No CLR lowering in kotc.
	}

	// DotKt round-trip: a call to a top-level function restored from a [KotlinFile] facade in a referenced
	// assembly -> a .NET static call on that file-facade class. `body == null` distinguishes the injected symbol
	// from a same-named local top-level fun. (A suspend top-level fun awaits via the coroutine path, not here.)
	if (callee.body == null && dispatchReceiver(call) == null) {
		val extRecv = extensionReceiver(call)
		// A2 stage 3: read the restored top-level function's .NET file-facade class off its RESOLVED IR `CallableId`
		// (`package` + name). FIR/Fir2Ir already resolved this call to a UNIQUE callee, so there is nothing to
		// disambiguate (a single fileClass per CallableId). `suspend` is read straight
		// off the resolved callee by `suspendCallTag(callee)` below.
		(callee.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)
			?.let { kotc.frontend.clrInjectedTopLevelFileClass(CallableId(it.packageFqName, callee.name), regularParams(callee).size, injectedExtReceiverKey(callee)) }?.let { fileClass ->
			// A FACADEGEN-INJECTED cross-module `inline fun` taking ANY lambda arg (AXIS ①) MUST be source-inlined: emit a
			// generic `callInline` node carrying the call bindings; bir2cir OWNS the splice (it re-lowers the carried body
			// in the app context, so a non-local `return`/`break`/suspend through a spliced lambda works, and a noinline
			// arg rides as a delegate — AXIS ②). A lambda-less inline call is NOT gated here — it falls through to the
			// plain call below, where the callee is a real generic method (the JIT inlines it). This fires ONLY for a
			// facadegen-named fileClass; the receiver-carrying stdlib scope/util fns have no fileClass and take the
			// owner-less path below. An EXTENSION receiver (`Cell<T>.update { … }`, #133 case1) rides through
			// `inlineSpliceCall` in `recvs.extension` — the SAME shape the owner-less path threads, spliced onto payload
			// param[0] (`__self`) by bir2cir.
			if (callNeedsSplice(call)) return inlineSpliceCall(call, fileClass)
			// PLAIN static call by identity to the referenced .NET file class (bir2cir's NetInteropBinding shapes it
			// to clrStatic / clrGenericStatic). This is the fall-through for a lambda-less inline call (the callee is a
			// real generic method) as well as every non-inline top-level fun.
			return plainInjectedTopLevelCall(call, callee, fileClass, name, extRecv)
		}
		// Any OTHER cross-module inline+lambda fun with no facadegen fileClass — the whole stdlib rides the klib, so
		// scope/util fns (let/run/with/apply/also/use), collection ops (forEach/map/filter), takeIf/takeUnless,
		// require/check, Result extensions, etc. all land here. Gate on `callNeedsSplice` (AXIS ①): ANY lambda arg emits
		// the OWNER-LESS `callInline` node — bir2cir resolves the hosting file class from the ref.dll [KotlinInline]
		// index (keyed name|pc|ga, disambiguated by a structural `paramSig` match) and splices the raw-BIR body (an
		// extension receiver rides in `recvs.extension`, `with`'s receiver as a regular arg; a noinline lambda rides as a
		// delegate — AXIS ②). There is NO @InlineOnly restriction (a plain `xs.forEach { return }` splices like any
		// other). A lambda-less inline call falls through to the plain callStatic below (the callee's real generic body
		// runs = status quo).
		if (callNeedsSplice(call)) return inlineSpliceCallOwnerless(call, extRecv)
	}
	// Fill omitted constant default arguments at the call site (IL methods have no default mechanism).
	val args = filledArgs(call).joinToString(",")
	// A generic method `fun <T> id(...)` -> carry the resolved type args so ilemit can MakeGenericMethod.
	val ta = typeArgsJson(call)
	// PLAIN Kotlin return type for the retType hint; a `suspend` callee is flagged by `suspendCallTag` on the node
	// (the kickoff/Task/await lowering is a deferred downstream layer). kotc bakes no coroutine ABI here.
	val effRet = birType(call.type)
	val recv = dispatchReceiver(call)
	// #199 DESIGN B — TWO-AXIS top-level call encoding. `owner:null` is LOAD-BEARING BIR vocabulary meaning "this is
	// a top-level call": ~12 bir2cir recognizers key on it (@ClrIntrinsic/@ClrCollectionFactory/@ClrArrayFactory
	// substitution, Precondition/Repeat/Enum/ForIn/CharSeq lowerings, …). So a same-module top-level call KEEPS
	// `owner:null` (the substitution/recognition axis — UNTOUCHED) and instead carries `calleeOwner`, the
	// FIR-resolved callee file-class (the mandatory DISPATCH axis — the owner-null recognition machinery IGNORES it, while
	// ilemit's dispatch consults it, mirroring `sty`). That disambiguates two same-simple-name top-level funcs in
	// DIFFERENT packages (a.foo/b.foo both emit `method:foo`) without shadowing substitution. See `calleeOwnerTag`.
	// An extension function: the receiver is the `__self` first arg. TOP-LEVEL `fun T.f()` -> static `f(self,args)`.
	// MEMBER `class C { fun T.f() }` has BOTH receivers -> instance method on the enclosing C (dispatch receiver),
	// with the extension receiver as the first arg (mirrors the JVM `C.f(T $receiver)` shape).
	val extRecv = extensionReceiver(call)
	if (extRecv != null) {
		val all = (listOf(expr(extRecv)) + filledArgs(call)).joinToString(",")
		if (recv != null) {
			val ownerStr = ownerSpec(declaringClass, recv.type)
			val virtual = isVirtualInstanceCall(call, callee)
			return """{"k":"callInstance","ownerType":${ownerStr.toJson()},"virtual":$virtual,"recv":${expr(recv)},"method":${str(name)}${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty() || (ownerStr as? TypeNode.Fqn)?.args != null, effRet)},"args":[$all]${suspendCallTag(callee)}${superTag(call)}}"""
		}
		return """{"k":"callStatic","owner":null,"method":${str(name)}${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty(), effRet)},"args":[$all]${suspendCallTag(callee)}${calleeOwnerTag(callee)}}"""
	}
	// Instance method on a user class, or a sibling top-level call.
	return if (recv != null) {
		// `it.hasNext()`/`it.next()` on a Kotlin iterator, `xs.iterator()` on a Kotlin iterable dispatch on the REAL
		// generic identity via ownerSpec below (`kotlin.collections.Iterator[int]` / `Iterable[int]`) — bir2cir
		// substitutes/normalizes them (no monomorphized synthetic; #58).
		val ownerStr = ownerSpec(declaringClass, recv.type)
		val virtual = isVirtualInstanceCall(call, callee)
		// An override of kotlin.Any's universal method (toString/equals/hashCode) carries `anySlot:true` — a pure-
		// Kotlin fact; bir2cir renames it to the System.Object slot. The Kotlin<->CLR name binding for any other
		// interface member is bir2cir's too.
		val mname = name
		val anySlotTag = if (isAnySlotMethod(callee)) ""","anySlot":true""" else ""
		// Carry the return type so ilemit can fall back to dynamic dispatch if static resolution fails AND the owner
		// implements a BCL clrg: interface (a substituted Kotlin collection whose member -- get_Item, iterator, addAll
		// -- lives on the BCL interface FindMethod skips). ilemit gates on the owner-interface so non-collection misses
		// still throw. See ilemit EmitDynamicCall.
		val dynRet = ""","dynRet":${birType(call.type).toJson()}"""
		"""{"k":"callInstance","ownerType":${ownerStr.toJson()},"virtual":$virtual,"recv":${recvExpr(recv, ownerStr, declaringClass?.defaultType)},"method":${str(mname)}${overloadSigField(callee)}$ta$dynRet${retHintStr(ta.isNotEmpty() || (ownerStr as? TypeNode.Fqn)?.args != null, effRet)},"args":[$args]${suspendCallTag(callee)}${overridesJson(callee)}$anySlotTag${superTag(call)}}"""
	} else """{"k":"callStatic","owner":null,"method":${str(name)}${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty(), effRet)},"args":[$args]${suspendCallTag(callee)}${calleeOwnerTag(callee)}}"""
}

/**
 * The PLAIN `callStatic` node for a call to a top-level function restored from a `[KotlinFile]` facade on a
 * referenced assembly (owner = the .NET file-facade type; bir2cir's NetInteropBinding shapes it to
 * clrStatic / clrGenericStatic). This is the fall-through for a lambda-less facadegen inline call (the callee is a
 * real generic method the JIT inlines) as well as every ordinary non-inline top-level fun.
 */
internal fun BirEmitter.plainInjectedTopLevelCall(call: IrCall, callee: IrSimpleFunction, fileClass: String, name: String, extRecv: IrExpression?): String {
	// A GENERIC top-level fun (e.g. a `reified` inline restored as a generic method) -> a generic static
	// call carrying the type args, so ilemit MakeGenericMethods it (the reified `typeof(T)`/`is T` body
	// then sees the concrete type). CLR generics are reified, so no inlining is needed across assemblies.
	if (callee.typeParameters.isNotEmpty()) {
		val targs = callee.typeParameters.indices.map { call.typeArguments.getOrNull(it) }
		if (targs.all { it != null }) {
			// An extension fun: its receiver is the .NET method's first param (`__self`), so prepend it to the args.
			// Keep injected args as BIR strings: a non-constant cross-module default has no honest IrExpression and is
			// represented by a positional `defaultArg` for bir2cir to splice from `[KotlinDefault]`.
			val a = listOfNotNull(extRecv?.let { expr(it) }) + filledInjectedArgs(call)
			val taJson = targs.joinToString(",") { birType(it!!).toJson() }
			// `shapeTypes` must line up with `a` (= extension receiver, then regular args), so a GENERIC extension
			// fun's `__self` receiver type is included — else bir2cir's by-shape overload pick finds 0 params.
			// PURE-KOTLIN `birType` identities; bir2cir derives the ilemit `shapes` tokens (see the member path above).
			val shapeParams = (if (extRecv != null) listOf(callee.parameters.first { it.kind == IrParameterKind.ExtensionReceiver }) else emptyList()) + regularParams(callee)
			val shapeTypes = shapeParams.joinToString(",") { birType(it.type).toJson() }
			// A2 (#61): a PLAIN static call by identity carrying the generic facts (typeArgs + shapeTypes);
			// bir2cir's NetInteropBinding resolves the file-class owner off the refs and shapes it to clrGenericStatic.
			return """{"k":"callStatic","ownerType":${fqnJson(fileClass)},"method":${str(name)},"typeArgs":[$taJson],"shapeTypes":[$shapeTypes],"args":[${a.joinToString(",")}]${suspendCallTag(callee)}}"""
		}
	}
	// A2 (#61): a PLAIN static call by identity to the referenced .NET file class; bir2cir's NetInteropBinding
	// shapes it to clrStatic. A `suspend` callee is flagged by `suspendCallTag` (Task/await lowering deferred).
	val ret = birType(callee.returnType)
	// #146: build the regular args as STRINGS so an omitted NON-CONST default emits a `defaultArg` placeholder (bir2cir's
	// DefaultArgSplice fills it from the callee's ref.dll @KotlinDefault). The extension receiver (arg[0] = `__self`) is
	// prepended; each arg's type is its PARAMETER's type (a placeholder carries no expr type). `sig` (the callee's full
	// .NET signature) drives DefaultArgSplice's arg-count match against the ref.dll @KotlinDefault key.
	val regArgs = filledInjectedArgs(call)
	val extStr = extRecv?.let { expr(it) }
	val argStrs = (listOfNotNull(extStr) + regArgs).joinToString(",")
	val extParamType = callee.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }?.let { birType(it.type) }
	val argTypeNodes = (listOfNotNull(extParamType) + regularParams(callee).map { birType(it.type) }.take(regArgs.size)).joinToString(",") { it.toJson() }
	return """{"k":"callStatic","ownerType":${fqnJson(fileClass)},"method":${str(name)}${overloadSigField(callee)},"argTypes":[$argTypeNodes],"ret":${str(ret)},"args":[$argStrs]${suspendCallTag(callee)}}"""
}

/**
 * `,"ret":${fqnJson("kotlin.Int")}` for a generic call/member access: the concrete result type is known here (FIR-resolved
 * `call.type`), so ilemit need not reflect the un-baked builder's return type (which stays `!0`/`!!0` and
 * would mis-drive value-type boxing). Only emitted for the generic/constructed paths to stay non-invasive.
 */
internal fun BirEmitter.retHint(generic: Boolean, t: IrType): String =
	if (generic) ""","ret":${birType(t).toJson()}""" else ""

/** Like [retHint] but with a pre-computed return-type string (e.g. a suspend call's kickoff `Task<T>`). */
internal fun BirEmitter.retHintStr(generic: Boolean, ret: TypeNode): String =
	if (generic) ""","ret":${ret.toJson()}""" else ""

/** Neutral metadata tag marking a call whose callee is a `suspend` function. kotc records only the FACT
 *  (mirroring the `"suspend":true` fn-decl flag); the coroutine LOWERING (await / state machine / Task ABI)
 *  is a DEFERRED downstream layer that consumes this tag. kotc does NO coroutine lowering. */
internal fun BirEmitter.suspendCallTag(callee: org.jetbrains.kotlin.ir.declarations.IrFunction): String =
	if ((callee as? IrSimpleFunction)?.isSuspend == true) ""","suspendCall":true""" else ""

/** #199/#204: the `,"calleeOwner":<fileClassFqn>` mandatory DISPATCH identity on a top-level `callStatic` whose `owner` stays
 *  `null`. `owner:null` is the load-bearing "top-level call" axis ~12 bir2cir owner-null recognizers key on
 *  (@ClrIntrinsic/@ClrCollectionFactory/@ClrArrayFactory substitution, Precondition/Repeat/Enum/ForIn/CharSeq
 *  lowerings, …); calleeOwner is a SEPARATE axis those passes ignore (they carry it through DeepClone or
 *  legitimately drop it when replacing a recognized call). ONLY ilemit's callStatic dispatch consults it — mirroring
 *  `sty`, a frontend-resolved per-node fact consumed downstream without re-resolution. Same-module declarations use
 *  their real IrFile; facadegen-injected cross-module functions use the injected file class. Other cross-module calls
 *  may omit it only while still BIR owner:null: bir2cir must replace them with an explicit owner before the CIR sanity
 *  boundary. `decl` is the callee function (or, for a top-level
 *  extension property accessor, the property itself — its file class holds the static accessor). */
internal fun BirEmitter.calleeOwnerTag(decl: org.jetbrains.kotlin.ir.declarations.IrDeclaration): String {
	val owner = if (decl.parent is IrFile) fileClassOf(decl) else (decl as? IrSimpleFunction)?.let { fn ->
		(fn.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)?.let { pkg ->
			kotc.frontend.clrInjectedTopLevelFileClass(
				CallableId(pkg.packageFqName, fn.name), regularParams(fn).size, injectedExtReceiverKey(fn))
		}
	}
	return owner?.let { ""","calleeOwner":${fqnJson(it)}""" } ?: ""
}

/** The owner of a static lift synthesized into the file currently being emitted. Unlike [calleeOwnerTag], this is
 *  never optional: the producer created the target method itself, so its exact file-class identity is known. */
internal fun BirEmitter.localCalleeOwnerTag(): String = ""","calleeOwner":${fqnJson(fileClass)}"""

/** `,"super":true` on a `super.X()` callInstance (issue #14). kotc already forces `virtual:false` here
 *  (isVirtualInstanceCall), but that non-virtual intent is LOST when a CLR-binding pass in bir2cir reshapes the
 *  node to a `clrInstance`/`clrPropGet` (NetInteropBinding / MemberCallSubstitution drop the `virtual` field). This
 *  marker RIDES ALONG so those passes can re-stamp the produced CLR node non-virtual, and ilemit emits `call`
 *  (not `callvirt`) for a reference owner — a base-slot dispatch exactly like C#'s `base.M()`. Without it a super
 *  call to a CLR-bound base (kotlin.Any/System.Object, a facadegen-injected .NET base, a @ClrTypeAlias stdlib base)
 *  callvirt-re-dispatches to THIS class's override -> infinite recursion. */
internal fun BirEmitter.superTag(call: IrCall): String =
	if (call.superQualifierSymbol != null) ""","super":true""" else ""

/** `,"typeArgs":["int"]` when the callee is a generic method (its own type params resolved at this call). */
internal fun BirEmitter.typeArgsJson(call: IrCall): String {
	val tps = call.symbol.owner.typeParameters
	if (tps.isEmpty()) return ""
	val args = tps.indices.map { call.typeArguments.getOrNull(it) }
	if (args.any { it == null }) return ""
	return ""","typeArgs":[${args.joinToString(",") { birType(it!!).toJson() }}]"""
}

/** The `byref(x)` marker intrinsic wrapping an arg -> the inner lvalue `x`; else null. Matched by FULL name
 *  (`kotlin.clr.byref`) so a user function happening to be named `byref` is not mistaken for the intrinsic. */
internal fun BirEmitter.byrefMarker(a: IrExpression): IrExpression? =
	if (a is IrCall && a.symbol.owner.fqNameWhenAvailable?.asString() == "kotlin.clr.byref") regularArgs(a).firstOrNull() else null

/** A stdlib byref parameter marked `@kotlin.clr.ClrRefArgument`: its argument is passed BY REFERENCE to the bound
 *  BCL member (bir2cir wraps the arg position `byref:` at substitution). kotc reads it ONLY to SHAPE the argument
 *  addressably — the byref call-substitution decision itself is bir2cir's. */
internal fun BirEmitter.isClrRefArgument(p: IrValueParameter): Boolean =
	p.annotations.any { it.type.classFqName?.asString() == "kotlin.clr.ClrRefArgument" }

/** Emit one regular call argument as its ADDRESSABLE lvalue (a property's backing FIELD node, else the lvalue
 *  itself) when the matching callee parameter is byref, so ilemit's EmitArg(want.IsByRef) can `ldflda`/`ldloca` it.
 *  Two byref shapes: a USER `ClrRef<T>` param (`byref:`) unwraps its explicit `byref(x)` marker; a STDLIB
 *  `@ClrRefArgument` param (a PLAIN type, no marker) shapes the bare arg directly — the stdlib's @ClrIntrinsic
 *  Interlocked/TryParse/DivRem helpers, plain calls in the ref build, substituted to BCL `ref`/`out` calls by
 *  bir2cir in the rt build. A non-byref parameter is unaffected (inert for every existing call). */
internal fun BirEmitter.argExpr(arg: IrExpression, param: IrValueParameter?): String {
	if (param != null) {
		if (birType(param.type) is TypeNode.ByRef) byrefMarker(arg)?.let { inner ->
			return byrefBackingField(inner) ?: expr(inner)
		}
		else if (isClrRefArgument(param)) return byrefBackingField(arg) ?: expr(arg)
		// A value-type-nullable arg (`Int?` smart-cast to `Int`) passed to a non-null value param must UNWRAP
		// `Nullable<T>.Value` — the CLR twin of JVM's implicit `Integer.intValue()` arg coercion (no IR node). C1.
		if (!isPreUnwrappedRead(arg)) nullableValueUnwrapElem(arg.type, param.type)?.let { elem ->
			return """{"k":"nullableValue","elem":${str(elem)},"e":${expr(arg)}}"""
		}
		// A boxed Any operand (an un-narrowed smart-cast, `x is Int && f(x)`) passed to a concrete value-primitive
		// param -> cast to the param type so the VALUE, not the box, reaches the slot. This is the arg twin of
		// recvExpr's boxed-Any coercion: a primitive operator (`a + b`) lowered by bir2cir flows its arg through here.
		if (param.type.isPrimitiveOrUnsigned() && birType(arg.type) == OBJ)
			return """{"k":"cast","type":${str(birType(param.type))},"e":${expr(arg)}}"""
	}
	return expr(arg)
}

/** Read the RECEIVER of a member call on a value-type primitive as its BARE VALUE: a value-nullable (`Int?`)
 *  smart-cast surfaces `Nullable<T>.Value`; a boxed `Any` smart-cast casts to the primitive. The receiver-slot
 *  twin of [argExpr]'s value coercion — a member call on `kotlin.Int`/`kotlin.Char`/… (a primitive
 *  operator, `compareTo`, `toString`, …) needs the raw value, not a `Nullable<T>` struct load / a box. A no-op
 *  for any non-primitive owner. */
internal fun BirEmitter.recvExpr(recv: IrExpression, ownerType: TypeNode, ownerIr: IrType?): String {
	// The owner's value-primitive-ness is read from the IR (`ownerIr` = the member's declaring class, or the
	// receiver's own type when the receiver was boxed to Any) — no kotlin.* primitive FQN table.
	val ownerPrim = ownerIr?.isPrimitiveOrUnsigned() == true || recv.type.isPrimitiveOrUnsigned()
	if (!ownerPrim || isPreUnwrappedRead(recv)) return expr(recv)
	nullableElem(recv.type)?.let { elem -> return """{"k":"nullableValue","elem":${str(elem)},"e":${expr(recv)}}""" }
	if (birType(recv.type) == OBJ) return """{"k":"cast","type":${str(ownerType)},"e":${expr(recv)}}"""
	return expr(recv)
}

/** A `byref(...)` target that is an own-source-set property read -> its BACKING-FIELD node, so ilemit takes the
 *  field address (`ldflda <backing>`) instead of addressing an accessor's return value (Phase 5). The field is
 *  INTERNAL, hence reachable across types in-module. Null for a non-property, a .NET/injected property, or a
 *  computed/delegated/lateinit/@ClrField property (no plain in-module backing field to address). */
internal fun BirEmitter.byrefBackingField(inner: IrExpression): String? {
	val call = inner as? IrCall ?: return null
	val callee = call.symbol.owner
	val prop = callee.correspondingPropertySymbol?.owner ?: return null
	if (callee !== prop.getter) return null
	val cls = callee.parent as? IrClass ?: return null
	if (isExternalNetType(cls)) return null
	if (prop.backingField == null || prop.isDelegated || prop.isLateinit || isClrField(prop)) return null
	val recv = dispatchReceiver(call)?.let { expr(it) } ?: """{"k":"this"}"""
	val owner = ownerSpec(cls, dispatchReceiver(call)?.type).toJson()
	return """{"k":"field","ownerType":$owner,"recv":$recv,"name":${str(prop.name.asString())}}"""
}

/** (argsJson, argTypesJson) for an injected .NET call. A `ClrRef<T>` param already maps to `byref:T` via birType
 *  (so the out/ref overload resolves + optional params still default-fill); a `byref(x)` arg unwraps to its lvalue
 *  `x`, which ilemit passes by address (EmitArg routes an IsByRef param through EmitAddr). */
internal fun BirEmitter.clrCallArgs(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression, callee: org.jetbrains.kotlin.ir.declarations.IrFunction): Pair<String, String> {
	val params = callee.parameters.filter { it.kind == IrParameterKind.Regular }
	val tj = params.map { birType(it.type).toJson() }
	val aj = regularArgs(call).map { val inner = byrefMarker(it); if (inner != null) (byrefBackingField(inner) ?: expr(inner)) else expr(it) }
	return aj.joinToString(",") to tj.joinToString(",")
}

// #82: whether an IrProperty is backed by a REAL static field vs a COMPUTED property whose cross-module
// deserialized stub carries a phantom backingField. Source IR: backingField is ground truth. A metadata
// (Fir2IrLazyProperty) stub: trust the deserialized FIR accessor kind — Fir2IrLazyProperty materializes a
// spurious IrField for any bodyless custom getter, and keeps IR_EXTERNAL_DECLARATION_STUB origin on BOTH
// default and custom accessors, so getter.origin cannot discriminate; FirDefaultPropertyGetter can.
private fun BirEmitter.hasRealStaticField(prop: IrProperty): Boolean {
	(prop as? org.jetbrains.kotlin.fir.lazy.Fir2IrLazyProperty)?.fir?.let { fir ->
		return fir.getter == null || fir.getter is org.jetbrains.kotlin.fir.declarations.impl.FirDefaultPropertyGetter
	}
	return prop.backingField != null
}

// #89: whether a property's GETTER is DEFAULT (kotc-generated trivial `field` passthrough). A property may
// have BOTH a real static backing field (an initializer) AND a custom `get() = field + 1` — reading it as a
// raw static-field load would skip the getter (the bug). So a top-level/companion property is only read as a
// static field when it has a real field AND a default getter; a custom getter must be invoked. For a
// same-module source property the accessor origin discriminates; a cross-module Fir2IrLazyProperty stub keeps
// IR_EXTERNAL_DECLARATION_STUB origin on both kinds, so trust the deserialized FIR accessor kind instead.
internal fun BirEmitter.hasDefaultGetter(prop: IrProperty): Boolean {
	(prop as? org.jetbrains.kotlin.fir.lazy.Fir2IrLazyProperty)?.fir?.let { fir ->
		return fir.getter == null || fir.getter is org.jetbrains.kotlin.fir.declarations.impl.FirDefaultPropertyGetter
	}
	val g = prop.getter ?: return true
	return g.origin == org.jetbrains.kotlin.ir.declarations.IrDeclarationOrigin.DEFAULT_PROPERTY_ACCESSOR
}

// #89 (write side): whether a property's SETTER is DEFAULT. A `var x = init; set(v) { field = v.trim() }` has a
// real field AND a custom setter — writing it as a raw static-field store would skip the setter. Symmetric to
// hasDefaultGetter.
internal fun BirEmitter.hasDefaultSetter(prop: IrProperty): Boolean {
	(prop as? org.jetbrains.kotlin.fir.lazy.Fir2IrLazyProperty)?.fir?.let { fir ->
		return fir.setter == null || fir.setter is org.jetbrains.kotlin.fir.declarations.impl.FirDefaultPropertySetter
	}
	val s = prop.setter ?: return true
	return s.origin == org.jetbrains.kotlin.ir.declarations.IrDeclarationOrigin.DEFAULT_PROPERTY_ACCESSOR
}

// #89: a property whose backing field is accessed THROUGH `field`-based get_/set_ accessors — the routing this
// fix targets. Excludes the canonical non-field-routed kinds (mirrors the member-accessor `emitsGet`/`emitsSet`
// exclusion set): `const` is frontend-inlined; `lateinit` keeps a raw null-checked field with default accessors;
// a DELEGATED property's `$delegate` field is NOT the value and its accessor lowering (the @InlineOnly
// `getValue`/`setValue` inline splice) is resolved only at DIRECT access sites, not inside an emitted accessor
// body — so #89 leaves delegation on its prior path rather than emit a half-lowered accessor; `@ClrField` is a
// plain field by opt-in. For an excluded property #89 reduces to the pre-fix rule (static field iff a real field
// exists); only a genuine `field`-routed property additionally consults accessor-defaultness.
internal fun BirEmitter.fieldRoutedProperty(prop: IrProperty): Boolean =
	!prop.isConst && !prop.isLateinit && !prop.isDelegated && !isClrField(prop)
// #89: a property READ resolves to a raw static-field load only with a real field AND (for a field-routed
// property) a default getter. An excluded (const/lateinit/delegated/@ClrField) property keeps the pre-fix rule.
private fun BirEmitter.readsAsStaticField(prop: IrProperty): Boolean =
	hasRealStaticField(prop) && (!fieldRoutedProperty(prop) || hasDefaultGetter(prop))
// #89: a property WRITE resolves to a raw static-field store only with a real field AND (for a field-routed
// property) a default setter.
private fun BirEmitter.writesAsStaticField(prop: IrProperty): Boolean =
	hasRealStaticField(prop) && (!fieldRoutedProperty(prop) || hasDefaultSetter(prop))
