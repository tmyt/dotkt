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
 * Inline a scope function `recv.let/run/with/apply/also { ... }` to a value-block: bind the receiver to
 * a unique local, rewrite `it`/`this` to it, then yield the lambda's last expression (let/run/with) or
 * the receiver (apply/also). No delegate — the lambda body is spliced in directly.
 */
internal fun BirEmitter.inlineScope(fq: String, recvExpr: IrExpression, lambda: IrFunctionExpression): String {
	val fn = lambda.function
	// A suspending call inside an INLINE scope-function lambda used as a sub-expression (e.g. an expression body
	// `= with(lib){ b.fetch() }`, or `c.apply{ s() }.x`) inlines to a value-block whose stmts/result span a
	// suspension. kotc emits that value-block VERBATIM (the suspend call keeps its `"suspendCall"` tag); the
	// downstream coroutine lowering (bir2cir SuspendColdLowering) flattens the value-block and segments the
	// suspension as an ordinary suspension point. kotc holds NO coroutine knowledge here (#11).
	val vname = "__scope${scopeCounter++}"
	val recvInit = expr(recvExpr)   // emit the receiver expression before binding `it`/`this`
	val recvParam = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
	val itParam = fn.parameters.firstOrNull { it.kind == IrParameterKind.Regular }
	// Save/restore (not remove) any prior binding of these names: a nested scope/let reusing the same param name
	// (`it`) must RESTORE the outer binding on exit, else statements AFTER this splice read a phantom local.
	val recvName = recvParam?.name?.asString(); val itName = itParam?.name?.asString()
	val hadRecv = recvName != null && valSubst.containsKey(recvName); val savedRecv = recvName?.let { valSubst[it] }
	val hadIt = itName != null && valSubst.containsKey(itName); val savedIt = itName?.let { valSubst[it] }
	recvParam?.let { valSubst[it.name.asString()] = """{"k":"local","name":${str(vname)}}""" }
	itParam?.let { valSubst[it.name.asString()] = """{"k":"local","name":${str(vname)}}""" }
	val stmts = (fn.body as? IrBlockBody)?.statements.orEmpty()
	val returnsRecv = fq == "kotlin.apply" || fq == "kotlin.also"
	val init = ArrayList<String>()
	init.add("""{"k":"var","name":${str(vname)},"type":${birType(recvExpr.type).toJson()},"init":$recvInit}""")
	val result: String
	if (returnsRecv) {
		stmts.forEach { if (it !is IrReturn) init.add(stmt(it)) }   // body is side-effects; Unit returns dropped
		result = """{"k":"local","name":${str(vname)}}"""
	} else {
		stmts.dropLast(1).forEach { init.add(stmt(it)) }
		result = when (val last = stmts.lastOrNull()) {
			is IrReturn -> expr(last.value)
			is IrExpression -> expr(last)
			else -> { last?.let { init.add(stmt(it)) }; """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}""" }
		}
	}
	recvName?.let { if (hadRecv) valSubst[it] = savedRecv!! else valSubst.remove(it) }
	itName?.let { if (hadIt) valSubst[it] = savedIt!! else valSubst.remove(it) }
	return """{"k":"valueBlock","stmts":[${init.joinToString(",")}],"result":$result}"""
}

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
	// Emit the count BEFORE binding the index param (mirrors inlineScope's recvInit): the count is arg 0, evaluated
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

/** `r.use { block }` -> a value-block: `var r; var res; try { res = block(r) } finally { r.Dispose() }; res`. */
internal fun BirEmitter.inlineUse(recvExpr: IrExpression, lambda: IrFunctionExpression, retType: TypeNode): String {
	val fn = lambda.function
	val uname = "__use${scopeCounter++}"; val rname = "__useRes${scopeCounter++}"
	val recvInit = expr(recvExpr)
	val itParam = fn.parameters.firstOrNull { it.kind == IrParameterKind.Regular }
	// Save/restore (not remove) any prior binding of `it`: a `use{}` nested inside another `it`-scope must RESTORE
	// the outer binding on exit, else statements after the splice read a phantom local.
	val itName = itParam?.name?.asString()
	val hadIt = itName != null && valSubst.containsKey(itName); val savedIt = itName?.let { valSubst[it] }
	itParam?.let { valSubst[it.name.asString()] = """{"k":"local","name":${str(uname)}}""" }
	val stmts = (fn.body as? IrBlockBody)?.statements.orEmpty()
	// kotc now emits the Kotlin FQN for source types, so a Unit-returning block's type is "kotlin.Unit"
	// (bir2cir lowers it to void). Accept the residual "void" shorthand too (synthetic/already-lowered rets).
	val unit = retType == TypeNode.Fqn("kotlin.Unit")
	val tryBody = ArrayList<String>()
	stmts.dropLast(1).forEach { tryBody.add(stmt(it)) }
	when (val last = stmts.lastOrNull()) {
		is IrReturn -> if (!unit) tryBody.add("""{"k":"setLocal","name":${str(rname)},"value":${expr(last.value)}}""") else last.value.takeIf { !it.type.isUnit() }?.let { tryBody.add("""{"k":"exprStmt","expr":${expr(it)}}""") }
		is IrExpression -> if (!unit) tryBody.add("""{"k":"setLocal","name":${str(rname)},"value":${expr(last)}}""") else tryBody.add("""{"k":"exprStmt","expr":${expr(last)}}""")
		else -> last?.let { tryBody.add(stmt(it)) }
	}
	itName?.let { if (hadIt) valSubst[it] = savedIt!! else valSubst.remove(it) }
	// The `use{}` try/finally structure is a language lowering that stays in kotc, but the `close()` call in the finally
	// is a PLAIN Kotlin member call on the kotlin.AutoCloseable receiver — bir2cir substitutes it to
	// System.IDisposable.Dispose() off the @ClrTypeAlias/@ClrIntrinsic("Dispose") binding (layer purity — no BCL name
	// in kotc). `use`'s signature (`T : AutoCloseable?`) guarantees the owner is kotlin.AutoCloseable.
	val dispose = """{"k":"exprStmt","expr":{"k":"callInstance","ownerType":${fqnJson("kotlin.AutoCloseable")},"method":"close","virtual":true,"recv":{"k":"local","name":${str(uname)}},"args":[]}}"""
	val tryNode = """{"k":"try","type":${fqnJson("kotlin.Unit")},"body":[${tryBody.joinToString(",")}],"catches":[],"finally":[$dispose]}"""
	val init = ArrayList<String>()
	init.add("""{"k":"var","name":${str(uname)},"type":${birType(recvExpr.type).toJson()},"init":$recvInit}""")
	if (!unit) init.add("""{"k":"var","name":${str(rname)},"type":${retType.toJson()}}""")
	init.add(tryNode)
	val result = if (unit) """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}""" else """{"k":"local","name":${str(rname)}}"""
	return """{"k":"valueBlock","stmts":[${init.joinToString(",")}],"result":$result}"""
}

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

/** CROSS-MODULE inline splice: a call to an injected `inline fun` (its body lives in [KotlinInline] on the
 *  referenced assembly, read by ilemit at splice time). We carry the call's bindings — each regular param's arg
 *  value, or for a lambda param the lambda's param name + body (emitted in the CALLER's scope, so a non-local
 *  `return` in it becomes the caller's return). ilemit substitutes these into the carried body. */
internal fun BirEmitter.inlineSpliceCall(call: IrCall, fileClass: String): String {
	val callee = call.symbol.owner
	val params = callee.parameters.filter { it.kind == IrParameterKind.Regular }
	val args = regularArgs(call)
	val bindings = params.mapIndexed { i, p ->
		val arg = args.getOrNull(i)
		if (arg is IrFunctionExpression) {
			val lamParam = arg.function.parameters.firstOrNull { it.kind == IrParameterKind.Regular }?.name?.asString() ?: "it"
			val body = bodyStatements(arg.function.body).joinToString(",") { stmt(it) }
			"""{"name":${str(p.name.asString())},"lambdaParam":${str(lamParam)},"lambdaBody":[$body]}"""
		} else """{"name":${str(p.name.asString())},"value":${arg?.let { expr(it) } ?: "null"}}"""
	}.joinToString(",")
	// An EXTENSION inline fun's body references the receiver via `this`; carry it so EmitInlineSplice can substitute it
	// (the body's `this` -> this bound value). Non-extension splices omit it (unchanged).
	val thisJson = extensionReceiver(call)?.let { ""","thisValue":${expr(it)}""" } ?: ""
	// Disambiguate the file-facade overload (forEach/count/... exist for Iterable/Array/CharSequence): the .NET method's
	// param count = regular params + the receiver-as-__self, and its generic arity = the fn's type params.
	val pc = params.size + (if (extensionReceiver(call) != null) 1 else 0)
	val ga = callee.typeParameters.size
	return """{"k":"inlineSplice","type":${str(fileClass)},"method":${str(callee.name.asString())},"pc":$pc,"ga":$ga,"bindings":[$bindings]$thisJson}"""
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
	if (!hasEarlyReturn(target.body, target.symbol)) return spliceBody(stmts, unit, pre)
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
