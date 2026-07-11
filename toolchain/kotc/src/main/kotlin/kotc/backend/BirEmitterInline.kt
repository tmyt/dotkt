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

/**
 * `repeat(n) { i -> body }` -> SPLICE the lambda body UN-CLOSURED into the caller (#75). Because the body is
 * emitted inline (not a delegate), a bare `return` inside it targets the ENCLOSING fn and stays a plain
 * `{k:return}` = a NON-LOCAL return (the fidelity #73/M7's delegate loop dropped). A `return@repeat` targets the
 * lambda fn and routes through `inlineReturnSubst` to a `goto <end>`. The index param resolves to the fresh loop
 * var. kotc carries the count + spliced body in a `callInline` node; bir2cir's InlineSplice wraps it in the counted
 * loop. This is pure Kotlin-language inlining — no CLR knowledge.
 */
internal fun BirEmitter.inlineRepeat(call: IrCall): String {
	val args = regularArgs(call)
	val countArg = args[0]
	val lambda = args[1] as IrFunctionExpression
	val fn = lambda.function
	val idxParam = fn.parameters.firstOrNull { it.kind == IrParameterKind.Regular }
	val loopVar = "__repIdx${scopeCounter++}"
	// Emit the count BEFORE binding the index param: the count is arg 0, evaluated
	// in the caller's scope, and could reference an outer binding that shares the index param's NAME (nested
	// `repeat(it){…}` / `forEach{ repeat(it){…} }`) — binding first would shadow it away.
	// Emit the count through argExpr against the callee's DECLARED first param (`times: Int`) — the SAME coercion the
	// old plain-call arg path applied: a value-type-nullable (`Int?`) count unwraps `Nullable<T>.Value` and an
	// un-narrowed boxed-`Any` smart-cast casts to the primitive, so a bare `int` reaches the loop bound (mirrors
	// RepeatInlineLowering's use of the declared `sig[0]`). argExpr is a no-op when the arg already matches.
	val timesParam = regularParams(call.symbol.owner).firstOrNull()
	val countJson = argExpr(countArg, timesParam)
	val countTypeJson = (if (timesParam != null) birType(timesParam.type) else birType(countArg.type)).toJson()
	// Save/restore the index-param binding (name-keyed): a nested repeat/scope reusing the same param name (`it`)
	// must not permanently clobber our outer binding for statements AFTER this call.
	val idxName = idxParam?.name?.asString()
	val savedIdx = idxName?.let { if (valSubst.containsKey(it)) valSubst[it] else null }
	val hadIdx = idxName != null && valSubst.containsKey(idxName)
	idxName?.let { valSubst[it] = """{"k":"local","name":${str(loopVar)}}""" }
	val end = cfgFresh()
	val saved = inlineReturnSubst[fn.symbol]
	inlineReturnSubst[fn.symbol] = null to end
	val pre = ArrayList<String>()
	bodyStatements(fn.body).forEach { pre.add(stmt(it)) }
	if (saved != null) inlineReturnSubst[fn.symbol] = saved else inlineReturnSubst.remove(fn.symbol)
	idxName?.let { if (hadIdx) valSubst[it] = savedIdx!! else valSubst.remove(it) }
	pre.add("""{"k":"label","id":$end}""")
	return """{"k":"callInline","callee":"kotlin.repeat","count":$countJson,"countType":$countTypeJson,"var":${str(loopVar)},"body":[${pre.joinToString(",")}]}"""
}

/** A `@kotlin.internal.InlineOnly` callee: an inline fun with no callable body on the JVM/CLR (its ONLY
 *  materialization is the spliced call site). The @InlineOnly cross-module inline+lambda funs — the scope
 *  functions (let/run/with/apply/also) and use{} — are routed through the generic owner-less `callInline`
 *  node (bir2cir splices their [KotlinInline] raw-BIR payloads off the ref.dll). Read straight off the
 *  klib callee's IR annotations (the klib preserves @InlineOnly). */
internal fun BirEmitter.isInlineOnly(callee: IrSimpleFunction): Boolean =
	callee.annotations.any { it.type.classFqName?.asString() == "kotlin.internal.InlineOnly" }

internal fun BirEmitter.hasLambdaArg(call: IrCall): Boolean = regularArgs(call).any {
	it is IrFunctionExpression || ((it as? IrGetValue)?.symbol?.owner?.let { owner -> inlineLambdas.containsKey(owner) } == true)
}

internal fun BirEmitter.nestedCapturesValue(node: IrElement?, decl: IrValueDeclaration): Boolean {
	var found = false
	node?.acceptChildrenVoid(object : IrVisitorVoid() {
		override fun visitElement(element: IrElement) {
			if (found) return
			when (element) {
				is IrFunctionExpression -> {
					if (decl in capturedVars(element.function, includeThis = true)) {
						found = true
						return
					}
				}
				is IrClass -> {
					if (decl in capturedVarsForObject(element)) {
						found = true
						return
					}
				}
			}
			element.acceptChildrenVoid(this)
		}
	})
	return found
}

/** Statements of a function/lambda body (block body, or a single-expression `= expr` body). */
internal fun BirEmitter.bodyStatements(body: org.jetbrains.kotlin.ir.IrElement?): List<org.jetbrains.kotlin.ir.IrStatement> = when (body) {
	is IrBlockBody -> body.statements
	is IrExpressionBody -> listOf(body.expression)
	else -> emptyList()
}

/**
 * Real inlining of a USER `inline fun` that takes a lambda arg ([[function-inlining-spike]]): bind non-lambda
 * params to temps and lambda params to the passed lambdas (in `inlineLambdas`), then splice the callee body as a
 * value-block. Invocations of a lambda param inside the body splice that lambda (see spliceLambdaCall); a
 * non-local `return` (already targeting the enclosing fun in the IR) returns from the caller since valueBlock is
 * inline. This also fixes mutable capture (the captured `var` is the caller's own local). lambda-less inline funs
 * never reach here — they emit as ordinary delegate-taking calls (the JIT inlines them).
 */
internal fun BirEmitter.inlineCall(call: IrCall): String {
	val callee = call.symbol.owner
	val extParam = callee.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
	val params = regularParams(callee)
	val extArg = extensionReceiver(call)
	val args = regularArgs(call)
	val pre = ArrayList<String>()
	val boundVals = ArrayList<String>(); val boundLams = ArrayList<org.jetbrains.kotlin.ir.declarations.IrValueDeclaration>()
	val oldVals = HashMap<String, String>()
	val hadOldVals = HashSet<String>()
	var boundExt = false
	fun bindVal(name: String, ref: String) {
		if (!boundVals.contains(name)) {
			if (valSubst.containsKey(name)) {
				hadOldVals.add(name)
				oldVals[name] = valSubst[name]!!
			}
			boundVals.add(name)
		}
		valSubst[name] = ref
	}
	// Substitute the callee's type params with the call's type args FIRST — before binding params — so both a bound
	// param's temp type (`birType(p.type)`) AND the spliced body resolve `gp:T` to the inferred type (a `*` star
	// projection with no concrete arg -> `object`/Any?). E.g. `with(e){…}`'s receiver temp gets `@Entry`, not `gp:T`.
	val tps = callee.typeParameters
	val subKeys = ArrayList<IrTypeParameter>()
	val calleeTypeArgs = HashMap<IrTypeParameter, TypeNode>()
	val oldTypeArgs = HashMap<IrTypeParameter, TypeNode?>()
	val hadOldTypeArg = HashSet<IrTypeParameter>()
	for (i in tps.indices) {
		val tp = tps[i]
		if (typeArgSubst.containsKey(tp)) {
			hadOldTypeArg.add(tp)
			oldTypeArgs[tp] = typeArgSubst[tp]
		}
		val ta = call.typeArguments.getOrNull(i)
		val bt = ta?.let { birType(it) }
		// "Self star" = the arg IS the callee's OWN type param (unresolved) -> object. Discriminate by SYMBOL
		// identity, not the token string: the CALLER's param with the SAME NAME also prints `gp:T`
		// (mapNotNullTo<T,..> body calling forEach<T> with the outer T) and is perfectly resolved — erasing it
		// to object detached the splice from the enclosing generic (Iterable[object] temp, object element into
		// Func<!!T,..>.Invoke -> InvalidProgramException).
		val selfOwned = ((ta as? IrSimpleType)?.classifierOrNull as? IrTypeParameterSymbol)?.owner?.parent == callee
		val subst = if (bt == null || selfOwned) OBJ else bt
		calleeTypeArgs[tp] = subst
		typeArgSubst[tp] = subst
		subKeys.add(tp)
	}
	fun restoreCalleeTypeArgs() {
		for (tp in subKeys) typeArgSubst[tp] = calleeTypeArgs[tp]!!
	}
	fun <T> withCallerTypeArgs(block: () -> T): T {
		for (tp in subKeys) {
			if (hadOldTypeArg.contains(tp)) typeArgSubst[tp] = oldTypeArgs[tp]!!
			else typeArgSubst.remove(tp)
		}
		return try { block() } finally { restoreCalleeTypeArgs() }
	}
	val callerTypeScope = BirEmitter.TypeArgScope(subKeys.toList(), HashMap(oldTypeArgs), HashSet(hadOldTypeArg))
	// A MEMBER inline fun's DISPATCH receiver must be bound like the extension receiver: the spliced body's
	// `this` (IrGetValue of the callee's dispatch param) otherwise falls through to the CALLER's `{"k":"this"}` —
	// `absoluteValue.toComponents { … }` inside Duration.toString read the NEGATIVE outer duration instead of
	// absoluteValue (printed "--1s"), and in a static caller a bare `this` is not even valid.
	val dispatchParam = callee.parameters.firstOrNull { it.kind == IrParameterKind.DispatchReceiver }
	val dispatchArg = dispatchReceiver(call)
	var boundDispatch = false
	if (dispatchParam != null && dispatchArg != null) {
		val tmp = "__inl${inlCounter++}"
		pre.add("""{"k":"var","name":${str(tmp)},"type":${birType(dispatchParam.type).toJson()},"init":${withCallerTypeArgs { expr(dispatchArg) }}}""")
		selfSubst[dispatchParam] = """{"k":"local","name":${str(tmp)}}"""
		boundDispatch = true
	}
	if (extParam != null && extArg != null) {
		val tmp = "__inl${inlCounter++}"
		pre.add("""{"k":"var","name":${str(tmp)},"type":${birType(extParam.type).toJson()},"init":${withCallerTypeArgs { expr(extArg) }}}""")
		val ref = """{"k":"local","name":${str(tmp)}}"""
		selfSubst[extParam] = ref
		if (extParam.name.asString() != "<this>") {
			bindVal(extParam.name.asString(), ref)
		}
		boundExt = true
	}
	for ((p, arg) in params.zip(args)) {
		// A `crossinline`/`noinline` lambda is NOT spliced: crossinline guarantees no non-local return (the only
		// reason to splice — see [[clr-not-jvm-discard-jvmisms]]) and noinline forbids inlining outright, and both
		// may be invoked from a nested lambda/object. Bind them to a real delegate local (the `else` path): the
		// arg emits as a closure, `block()` falls through to the delegate-invoke path, and a nested lambda/object
		// captures the local via the normal closure machinery.
		val inlineLambdaArg = when (arg) {
			is IrFunctionExpression -> arg
			is IrGetValue -> inlineLambdas[arg.symbol.owner]
			else -> null
		}
		if (inlineLambdaArg != null && !p.isCrossinline && !p.isNoinline && !nestedCapturesValue(callee.body, p)) {
			inlineLambdas[p] = inlineLambdaArg
			inlineLambdaTypeScopes[inlineLambdaArg] = callerTypeScope
			boundLams.add(p)
		}
		else {
			val tmp = "__inl${inlCounter++}"
			pre.add("""{"k":"var","name":${str(tmp)},"type":${birType(p.type).toJson()},"init":${withCallerTypeArgs { expr(inlineLambdaArg ?: arg) }}}""")
			bindVal(p.name.asString(), """{"k":"local","name":${str(tmp)}}""")
		}
	}
	val result = spliceBodyWithReturns(callee, callee.returnType.isUnit(), pre)
	boundVals.forEach { name -> if (hadOldVals.contains(name)) valSubst[name] = oldVals[name]!! else valSubst.remove(name) }
	boundLams.forEach { inlineLambdas.remove(it)?.let { lam -> inlineLambdaTypeScopes.remove(lam) } }
	subKeys.forEach { tp -> if (hadOldTypeArg.contains(tp)) typeArgSubst[tp] = oldTypeArgs[tp]!! else typeArgSubst.remove(tp) }
	if (boundExt) selfSubst.remove(extParam)
	if (boundDispatch) selfSubst.remove(dispatchParam)
	// The `suspendCoroutineUninterceptedOrReturn { c -> … }` intrinsic: after inlining, its fake body
	// (`throw NotImplementedError("… is intrinsic")`) survives as this valueBlock's result, and the crossinline
	// `block` is materialized as a closure captured into a dead __inlN. bir2cir recognizes such a block as a cold
	// suspension point. Stamp a STABLE `suspendIntrinsic:true` marker so bir2cir need not sniff the fake body's
	// thrown-message string (SuspendColdLowering.IsSuspendIntrinsicBlock prefers this flag; the string path is
	// legacy fallback). kotc emits the flag, NOT any CLR knowledge — it's a Kotlin-language intrinsic identity.
	val suspendIntrinsic = if (callee.fqNameWhenAvailable?.asString() ==
		"kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn") ""","suspendIntrinsic":true""" else ""
	return """{"k":"valueBlock","stmts":[${pre.joinToString(",")}],"result":$result$suspendIntrinsic}"""
}

internal fun <T> BirEmitter.withTypeArgScope(scope: BirEmitter.TypeArgScope?, block: () -> T): T {
	if (scope == null) return block()
	val saved = HashMap<IrTypeParameter, TypeNode?>()
	val hadSaved = HashSet<IrTypeParameter>()
	for (nm in scope.keys) {
		if (typeArgSubst.containsKey(nm)) {
			hadSaved.add(nm)
			saved[nm] = typeArgSubst[nm]
		}
		if (scope.had.contains(nm)) typeArgSubst[nm] = scope.old[nm]!!
		else typeArgSubst.remove(nm)
	}
	return try { block() } finally {
		for (nm in scope.keys) {
			if (hadSaved.contains(nm)) typeArgSubst[nm] = saved[nm]!!
			else typeArgSubst.remove(nm)
		}
	}
}

/** CROSS-MODULE inline: a call to an injected `inline fun` taking a lambda (its `[KotlinInline]` body lives on the
 *  referenced assembly). kotc emits a GENERIC `callInline` node carrying the call bindings — the type args, one entry
 *  per regular param (a literal lambda as an `inlineLambda` carrier, else the arg expr) — plus a `fallback` = the plain
 *  call kotc would otherwise emit. bir2cir OWNS the splice: it re-lowers the carried body in the app context (so it
 *  binds against app types) and drops/keeps the `fallback` on splice success/failure. This facadegen path is gated at
 *  the call site on `extRecv == null` (its receiver shape is untested — a receiver call takes the owner-less path
 *  instead), so `recvs` is always empty here. */
internal fun BirEmitter.inlineSpliceCall(call: IrCall, fileClass: String): String {
	val callee = call.symbol.owner
	val name = callee.name.asString()
	val extRecv = extensionReceiver(call)   // null here — the call-site gate guarantees it
	val params = callee.parameters.filter { it.kind == IrParameterKind.Regular }
	val args = regularArgs(call)
	// Disambiguate the file-facade overload (forEach/count/... exist for Iterable/Array/CharSequence): the .NET method's
	// param count = regular params + the receiver-as-__self, and its generic arity = the fn's type params. SAME as the
	// retired inlineSplice node's pc/ga.
	val pc = params.size + (if (extRecv != null) 1 else 0)
	val ga = callee.typeParameters.size
	// One type-arg entry per callee type param (a null/star projection -> object, mirroring inlineCall).
	val typeArgs = callee.typeParameters.indices.joinToString(",") { i ->
		(call.typeArguments.getOrNull(i)?.let { birType(it) } ?: OBJ).toJson()
	}
	// One entry per REGULAR param, in order: a literal lambda -> an `inlineLambda` carrier; any other arg -> its expr.
	val argsJson = params.indices.joinToString(",") { i ->
		val arg = args.getOrNull(i)
		if (arg is IrFunctionExpression) emitInlineLambdaCarrier(arg)
		else if (arg != null) expr(arg)
		else "null"
	}
	val retType = birType(callee.returnType).toJson()
	// The fall-through plain call (byte-identical to the un-gated path): bir2cir swaps it in on any splice failure.
	val fallback = plainInjectedTopLevelCall(call, callee, fileClass, name, extRecv)
	return """{"k":"callInline","callee":${str(callee.fqNameWhenAvailable?.asString() ?: name)},"owner":${str(fileClass)},"pc":$pc,"ga":$ga,"typeArgs":[$typeArgs],"recvs":{},"args":[$argsJson],"retType":$retType,"fallback":$fallback}"""
}

/** An `inlineLambda` carrier for a literal lambda arg of a cross-module inline call: the lambda's OWN params
 *  (name+type, bound by bir2cir at splice time) plus its body emitted IN THE CALLER'S SCOPE. `spliceBodyWithReturns`
 *  routes a labeled `return@callee` (target = the lambda fn symbol) through a result-local + end-label; a bare NON-LOCAL
 *  `return` (target = the enclosing caller) stays a plain `{"k":"return"}` — the caller's return, the point of inlining.
 *
 *  A RECEIVER lambda (`T.()->R`, from run/with/apply) carries its extension receiver as a LEADING carrier param:
 *  bir2cir binds it to invoke arg[0] (the stdlib scope-fn body passes the receiver as the invoke's first arg). The
 *  IR receiver name is the anonymous `<this>` (not emittable), so mint a FRESH name and bind the ext param's
 *  `this`-refs to it via `selfSubst` for the body emission (restored after). Without this a receiver lambda's
 *  `this`/implicit-member refs would fall through to the CALLER's `{"k":"this"}` and dangle. */
internal fun BirEmitter.emitInlineLambdaCarrier(lambda: IrFunctionExpression): String {
	val fn = lambda.function
	val extParam = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
	val freshRecv = extParam?.let { "__recv${inlCounter++}" }
	val hadSelf = extParam != null && selfSubst.containsKey(extParam)
	val savedSelf = extParam?.let { selfSubst[it] }
	if (extParam != null) selfSubst[extParam] = """{"k":"local","name":${str(freshRecv!!)}}"""
	val leading = if (extParam != null) listOf("""{"name":${str(freshRecv!!)},"type":${birType(extParam.type).toJson()}}""") else emptyList()
	val regularParams = fn.parameters.filter { it.kind == IrParameterKind.Regular }
	val regularJson = regularParams.map { p ->
		"""{"name":${str(p.name.asString())},"type":${birType(p.type).toJson()}}"""
	}
	val paramsJson = (leading + regularJson).joinToString(",")
	// SHADOW the lambda's own regular params in `valSubst` while emitting its body: an enclosing splice
	// (inlineRepeat / another lambda carrier) may have bound the SAME name (e.g. `it`) to an outer local. Without
	// removing it here, the body's ref to this lambda's param would resolve to the OUTER binding — the carrier param
	// is named correctly but the body dangles on a foreign local (`load unknown var __repIdx*`). Emitting them as
	// BARE `{"k":"local","name":<param>}` refs lets bir2cir bind the carrier param. (The ext-receiver already does
	// this via `selfSubst`.) Saved + restored around the body emission, mirroring `spliceLambdaCall`.
	val shadowed = regularParams.map { it.name.asString() }.associateWith { valSubst[it] }
	shadowed.keys.forEach { valSubst.remove(it) }
	val body = ArrayList<String>()
	// The `selfSubst[extParam]` binding above (restored just below) is the guarantee that the receiver's `this`/
	// implicit-member refs resolve to `freshRecv`, not a dangling `{"k":"this"}` — no post-hoc string guard needed.
	val result = spliceBodyWithReturns(fn, fn.returnType.isUnit(), body)
	shadowed.forEach { (name, prev) -> if (prev != null) valSubst[name] = prev else valSubst.remove(name) }
	if (extParam != null) { if (hadSelf) selfSubst[extParam] = savedSelf!! else selfSubst.remove(extParam) }
	return """{"k":"inlineLambda","params":[$paramsJson],"body":[${body.joinToString(",")}],"result":$result}"""
}

/** CROSS-MODULE inline of an `@kotlin.internal.InlineOnly` stdlib scope/util fn (let/run/with/apply/also/use) whose
 *  `[KotlinInline]` raw-BIR body lives on the ref.dll. Unlike `inlineSpliceCall`, kotc CANNOT name the hosting file
 *  class — the whole stdlib rides the klib, facadegen supplies no `kotlin.*` metadata — so the node is OWNER-LESS:
 *  bir2cir resolves the hosting file class from the ref.dll `[KotlinInline]` index by `callee|pc|ga`. An extension
 *  receiver (let/run/apply/also/use) rides in `recvs.extension`; `with`'s receiver is a REGULAR param and rides as a
 *  plain arg. The `fallback` = the plain owner-less `callStatic` kotc would emit un-spliced; bir2cir swaps it in on any
 *  splice failure (the rt.dll carries a real body for these funcs, so the fallback is a safe if NLR-losing plain call). */
internal fun BirEmitter.inlineSpliceCallOwnerless(call: IrCall, extRecv: IrExpression?): String {
	val callee = call.symbol.owner
	val name = callee.name.asString()
	val params = callee.parameters.filter { it.kind == IrParameterKind.Regular }
	val args = regularArgs(call)
	// The .NET method param count = regular params + the receiver-as-__self; generic arity = the fn's type params.
	// Matches the ref.dll payload key owner|name|pc|ga.
	val pc = params.size + (if (extRecv != null) 1 else 0)
	val ga = callee.typeParameters.size
	val typeArgs = callee.typeParameters.indices.joinToString(",") { i ->
		(call.typeArguments.getOrNull(i)?.let { birType(it) } ?: OBJ).toJson()
	}
	// Render the extension receiver ONCE and share the string between `recvs` and the `fallback` — emitting it twice
	// would re-run `expr` (re-registering its lifted lambdas/closures, and 2^N-exploding a chained `a.let{}.let{}`).
	val extRecvJson = extRecv?.let { expr(it) }
	val recvs = if (extRecvJson != null) """{"extension":$extRecvJson}""" else "{}"
	// One entry per REGULAR param, in order: a literal lambda -> an `inlineLambda` carrier; any other arg -> its expr
	// (for `with`, the receiver is regular param[0] and rides as a plain expr; the lambda is param[1]).
	val argsJson = params.indices.joinToString(",") { i ->
		val arg = args.getOrNull(i)
		if (arg is IrFunctionExpression) emitInlineLambdaCarrier(arg)
		else if (arg != null) expr(arg)
		else "null"
	}
	val retType = birType(callee.returnType).toJson()
	val fallback = plainOwnerlessTopLevelCall(call, callee, name, extRecvJson)
	return """{"k":"callInline","callee":${str(callee.fqNameWhenAvailable?.asString() ?: name)},"owner":null,"pc":$pc,"ga":$ga,"typeArgs":[$typeArgs],"recvs":$recvs,"args":[$argsJson],"retType":$retType,"fallback":$fallback}"""
}

/** The PLAIN owner-less `callStatic` a receiver-less/extension top-level fun would emit un-spliced (byte-identical to
 *  the general fall-through in `emitCall`: an extension receiver rides as the first arg). Used as the `fallback` slot
 *  of an owner-less `callInline` for the stdlib scope/util fns (owner=null: they resolve from the klib, not facadegen). */
internal fun BirEmitter.plainOwnerlessTopLevelCall(call: IrCall, callee: IrSimpleFunction, name: String, extRecvJson: String?): String {
	val ta = typeArgsJson(call)
	val effRet = birType(call.type)
	val all = (listOfNotNull(extRecvJson) + filledArgs(call)).joinToString(",")
	return """{"k":"callStatic","owner":null,"method":${str(name)}${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty(), effRet)},"args":[$all]${suspendCallTag(callee)}}"""
}

/** Splice an invoked inlined lambda `f(args)`: bind its params to the invoke args, then splice its body. */
internal fun BirEmitter.spliceLambdaCall(lambda: IrFunctionExpression, call: IrCall): String {
	val fn = lambda.function
	val extParam = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
	val params = fn.parameters.filter { it.kind == IrParameterKind.Regular }
	val args = regularArgs(call)
	val extArg = if (extParam != null) extensionReceiver(call) ?: args.firstOrNull() else null
	val regArgs = if (extParam != null && extArg != null && extensionReceiver(call) == null && args.firstOrNull() === extArg) args.drop(1) else args
	val pre = ArrayList<String>(); val bound = ArrayList<String>(); var boundExt = false
	if (extParam != null && extArg != null) {
		val tmp = "__lam${inlCounter++}"
		pre.add("""{"k":"var","name":${str(tmp)},"type":${birType(extParam.type).toJson()},"init":${expr(extArg)}}""")
		val ref = """{"k":"local","name":${str(tmp)}}"""
		selfSubst[extParam] = ref
		valSubst[extParam.name.asString()] = ref
		bound.add(extParam.name.asString())
		boundExt = true
	}
	for ((p, arg) in params.zip(regArgs)) {
		val tmp = "__lam${inlCounter++}"
		pre.add("""{"k":"var","name":${str(tmp)},"type":${birType(p.type).toJson()},"init":${expr(arg)}}""")
		valSubst[p.name.asString()] = """{"k":"local","name":${str(tmp)}}"""; bound.add(p.name.asString())
	}
	val result = withTypeArgScope(inlineLambdaTypeScopes[lambda]) {
		spliceBodyWithReturns(fn, fn.returnType.isUnit() || call.type.isUnit(), pre)
	}
	bound.forEach { valSubst.remove(it) }
	if (boundExt) selfSubst.remove(extParam)
	return """{"k":"valueBlock","stmts":[${pre.joinToString(",")}],"result":$result}"""
}

/** True iff [body] contains an IrReturn TARGETING [target] anywhere other than as the body's LAST top-level
 *  statement (spliceBody already folds a tail return into the value expression). Nested lambdas are walked too:
 *  a labeled return inside one can target the enclosing spliced fn. */
internal fun BirEmitter.hasEarlyReturn(body: org.jetbrains.kotlin.ir.IrElement?, target: org.jetbrains.kotlin.ir.symbols.IrReturnTargetSymbol): Boolean {
	val stmts = bodyStatements(body)
	val tail = stmts.lastOrNull()
	var found = false
	val walker = object : IrVisitorVoid() {
		override fun visitElement(element: org.jetbrains.kotlin.ir.IrElement) {
			if (found) return
			if (element is IrReturn && element.returnTargetSymbol == target) { found = true; return }
			element.acceptChildrenVoid(this)
		}
	}
	for (s in stmts) {
		// The tail return itself is fine (spliceBody folds it) — but its VALUE could still nest one.
		if (s === tail && s is IrReturn && s.returnTargetSymbol == target) s.value.acceptVoid(walker)
		else s.acceptVoid(walker)
		if (found) return true
	}
	return false
}

/**
 * spliceBody + EARLY-return support. A `return v` in the middle of a spliced inline body (indexOfLast's
 * `return index` inside its loop) must not emit a raw method return — the splice is a valueBlock INSIDE the
 * caller, so the raw return used the CALLER's frame (a void caller got an Int32 on the stack at ret:
 * kotlin.time.Duration.appendFractional, ilverify ReturnVoid + InvalidProgramException at run). Route every
 * return targeting the spliced fn through a RESULT LOCAL + an END LABEL (`res = v; goto end`; the natural tail
 * value assigns res too; the valueBlock result reads res after the label). Early-return-free bodies (the
 * overwhelmingly common case) keep the plain spliceBody shape — zero BIR churn.
 */
internal fun BirEmitter.spliceBodyWithReturns(target: IrSimpleFunction, unit: Boolean, pre: MutableList<String>): String {
	val stmts = bodyStatements(target.body)
	if (!hasEarlyReturn(target.body, target.symbol)) {
		// Unit fast path: the spliced fn's OWN implicit tail `return@lambda Unit` (returnTargetSymbol == target.symbol)
		// must be FOLDED into the Unit value — drop a plain Unit ref, evaluate a side-effecting value as an exprStmt —
		// NOT emitted as a bare `{"k":"return"}`. `spliceBody(unit=true)` would emit it raw via `stmt(IrReturn)` (no
		// inlineReturnSubst on this fast path), and bir2cir treats a bare `{"k":"return"}` as a CALLER non-local return
		// → a spurious `ret` in the caller frame (the `7.apply { }` line silently dropped). A tail return targeting the
		// ENCLOSING CALLER (returnTargetSymbol != target.symbol) is a real NLR and stays raw via `spliceBody`.
		if (unit) {
			val last = stmts.lastOrNull()
			if (last is IrReturn && last.returnTargetSymbol == target.symbol) {
				stmts.dropLast(1).forEach { pre.add(stmt(it)) }
				if (last.value !is IrGetObjectValue) pre.add("""{"k":"exprStmt","expr":${expr(last.value)}}""")
				return """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}"""
			}
		}
		return spliceBody(stmts, unit, pre)
	}
	val res = if (unit) null else "__inlRet${inlCounter++}"
	val end = cfgFresh()
	res?.let {
		val rt = birType(target.returnType)
		pre.add("""{"k":"var","name":${str(it)},"type":${str(rt)},"init":{"k":"default","type":${str(rt)}}}""")
	}
	val saved = inlineReturnSubst[target.symbol]
	inlineReturnSubst[target.symbol] = res to end
	// A NOTHING-typed tail expression (a when whose branches all return/throw) is a STATEMENT, not the splice
	// value — its returns route through the subst; reading it as the value would render IrReturn as an expr.
	val last = stmts.lastOrNull()
	val tail = if (!unit && last is IrExpression && last !is IrReturn && last.type.classFqName?.asString() == "kotlin.Nothing") {
		stmts.forEach { pre.add(stmt(it)) }
		null
	} else spliceBody(stmts, unit, pre)
	if (saved != null) inlineReturnSubst[target.symbol] = saved else inlineReturnSubst.remove(target.symbol)
	return if (res != null) {
		tail?.let { pre.add("""{"k":"setLocal","name":${str(res)},"value":$it}""") }
		pre.add("""{"k":"label","id":$end}""")
		"""{"k":"local","name":${str(res)}}"""
	} else {
		pre.add("""{"k":"label","id":$end}""")
		"""{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}"""
	}
}

/** Emit body statements into `pre`, returning the value expression (Unit -> void const; else the last expr). */
internal fun BirEmitter.spliceBody(stmts: List<org.jetbrains.kotlin.ir.IrStatement>, unit: Boolean, pre: MutableList<String>): String {
	if (unit) { stmts.forEach { pre.add(stmt(it)) }; return """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}""" }
	stmts.dropLast(1).forEach { pre.add(stmt(it)) }
	return when (val last = stmts.lastOrNull()) {
		is IrReturn -> expr(last.value)
		is IrExpression -> expr(last)
		else -> { last?.let { pre.add(stmt(it)) }; """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}""" }
	}
}

/** `stackBuffer(n) { buf -> body }` -> a scoped CLR stack allocation: declare a length + a localloc'd pointer,
 *  splice the (inline) block with `buf` bound to that allocation, return the block's result R. */
internal fun BirEmitter.emitStackBuffer(call: IrCall): String {
	val args = regularArgs(call)
	val lambda = args.getOrNull(1) as? IrFunctionExpression
		?: return unsupported(call, "stackBuffer", "its block must be a literal lambda (so it can be inlined into the caller's frame)")
	val fn = lambda.function
	val bufParam = fn.parameters.first { it.kind == IrParameterKind.Regular }
	val elemT = call.typeArguments.getOrNull(0)?.let { birType(it) } ?: OBJ
	val c = scopeCounter++
	val ptrName = "__sbp$c"; val lenName = "__sbl$c"
	val pre = arrayListOf(
		"""{"k":"var","name":${str(lenName)},"type":${fqnJson("kotlin.Int")},"init":${expr(args[0])}}""",
		"""{"k":"var","name":${str(ptrName)},"type":${fqnJson("stackptr")},"init":{"k":"stackAlloc","count":{"k":"local","name":${str(lenName)}},"elem":${str(elemT)}}}""")
	stackBufSubst[bufParam] = BirEmitter.StackBufInfo(ptrName, lenName, elemT)
	val result = spliceBody(bodyStatements(fn.body), fn.returnType.isUnit() || call.type.isUnit(), pre)
	stackBufSubst.remove(bufParam)
	return """{"k":"valueBlock","stmts":[${pre.joinToString(",")}],"result":$result}"""
}

/** A `StackBuffer<T>` member access (`buf[i]` / `buf[i]=v` / `buf.size`) inside the spliced block -> a stack op. */
internal fun BirEmitter.emitStackBufferOp(call: IrCall, callee: IrSimpleFunction, info: BirEmitter.StackBufInfo): String {
	val ptr = """{"k":"local","name":${str(info.ptrName)}}"""
	val len = """{"k":"local","name":${str(info.lenName)}}"""
	return when {
		callee.correspondingPropertySymbol?.owner?.name?.asString() == "size" -> len
		callee.name.asString() == "get" ->
			"""{"k":"stackGet","ptr":$ptr,"len":$len,"index":${expr(regularArgs(call)[0])},"elem":${str(info.elemT)}}"""
		callee.name.asString() == "set" ->
			"""{"k":"stackSet","ptr":$ptr,"len":$len,"index":${expr(regularArgs(call)[0])},"elem":${str(info.elemT)},"value":${expr(regularArgs(call)[1])}}"""
		// `buf.asSpan()` -> `new System.Span<T>(ptr, size)` over the stack memory (for .NET Span APIs).
		callee.name.asString() == "asSpan" -> """{"k":"stackAsSpan","ptr":$ptr,"len":$len,"elem":${str(info.elemT)}}"""
		else -> unsupported(call, "StackBuffer.${callee.name.asString()}", "only size / indexing / asSpan are supported")
	}
}
