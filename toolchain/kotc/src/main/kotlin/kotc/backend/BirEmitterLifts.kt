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
 * The enclosing type parameters a synthesized closure CLASS must be generic over: those referenced by its capture
 * field types (and its own parameter/return types). On the CLR generics are reified, so a closure that captures a
 * `T`-typed value (or a `List<T>` / `(T)->Unit`) becomes a SEPARATE class with a `gp:T` field — and `T` (an
 * enclosing *method* type parameter) is not in scope from inside that class. The closure class must therefore
 * declare `T` itself and be instantiated with the enclosing `T` at `newClosure`, or `MapType` fails to resolve it.
 */
private fun BirEmitter.freeTypeParams(types: List<IrType>): List<org.jetbrains.kotlin.ir.declarations.IrTypeParameter> {
	val acc = LinkedHashSet<org.jetbrains.kotlin.ir.declarations.IrTypeParameter>()
	fun walk(t: IrType) {
		(t.classifierOrNull as? IrTypeParameterSymbol)?.let { acc.add(it.owner) }
		if (t is IrSimpleType) t.arguments.forEach { (it as? IrTypeProjection)?.type?.let(::walk) }
	}
	types.forEach(::walk)
	return acc.toList()
}

/** Type operands USED in a function body (e.g. `x is R` / `x as R` / `R::class`). A lifted closure must be generic
 *  over these too: on the CLR generics are reified, so `is R` works once the lifted method carries `R` — unlike the
 *  JVM, which needs `reified`+inlining. freeTypeParams over (params+return+captures) alone misses a body-only `R`. */
internal fun BirEmitter.bodyTypeOperands(fn: org.jetbrains.kotlin.ir.declarations.IrFunction): List<IrType> {
	val out = ArrayList<IrType>()
	fn.body?.acceptVoid(object : IrVisitorVoid() {
		override fun visitElement(element: org.jetbrains.kotlin.ir.IrElement) {
			// Don't descend into NESTED lambdas/local funs — they compute their own free type params when lifted.
			if (element is IrFunctionExpression || element is org.jetbrains.kotlin.ir.declarations.IrFunction) return
			when (element) {
				is IrTypeOperatorCall -> out.add(element.typeOperand)
				is IrClassReference -> out.add(element.classType)
				else -> {}
			}
			element.acceptChildrenVoid(this)
		}
	})
	return out
}

/**
 * A `suspend` lambda literal -> the `newSuspendLambda` BIR node (the dormant bir2cir SuspendLambdaLowering consumer).
 * Emits ONLY pure Kotlin facts — captures, own params, result type, enclosing type-param names, and the body EXACTLY
 * as a suspend-fun body (its suspend calls already carry `"suspendCall":true`). bir2cir builds the `ContinuationImpl`
 * state machine (create/invokeSuspend/resume) from these; kotc bakes no coroutine ABI. Emits the pure facts for
 * ANY arity N — bir2cir's SuspendLambda create() protocol covers 0/1 (fixed create() slots) and >= 2 (the general
 * create(args, completion) slot); kotc no longer gates on arity.
 * Restricted-suspension builder lambdas (`@RestrictsSuspension` on the extension-receiver scope, e.g.
 * `sequence { }`/`iterator { }`'s `SequenceScope`) flow through THIS path too — bir2cir picks the
 * `RestrictedSuspendLambda` base from the scope's annotation. kotc has no `sequence`/`yield` knowledge.
 * Captures/params reuse the SAME machinery as the closure path (`capturedVars(includeThis=true)` / `captureFieldName`
 * / `captureFieldType`). NOTE: unlike newClosure, the body is emitted WITHOUT installing `captureSubst` — bir2cir's
 * SM builder rewrites captured-var reads (plain `{"k":"local"}`) into SM field reads itself. typeArgs are the BARE
 * enclosing type-param names (bir2cir prepends `gp:` when it instantiates the open SM), NOT the `gp:`-prefixed form
 * newClosure emits for ilemit.
 */
private fun BirEmitter.suspendLambda(node: IrFunctionExpression): String? {
	val fn = node.function
	// Own params in delegate order (extension receiver first, then regular) — matches lambdaParamsJson + bir2cir's
	// create() views (arity 0/1 = fixed create() slots, arity >= 2 = create(args, completion)). arity = the count.
	val ownParams = orderedLambdaParams(fn)
	// Restricted-suspension builders (`sequence { }`/`iterator { }`'s @RestrictsSuspension SequenceScope receiver)
	// now flow through this SAME suspend-lambda path: bir2cir gives the lambda the `RestrictedSuspendLambda` base
	// (not the plain SuspendLambda), so the cold-core builder runs. No exclusion here — kotc emits the pure suspend
	// facts and bir2cir picks the restricted base from the receiver scope's @RestrictsSuspension annotation.
	val captures = capturedVars(fn, includeThis = true)
	val capturesJson = captures.joinToString(",") { d ->
		"""{"name":${str(captureFieldName(d))},"type":${str(captureFieldType(d))}}"""
	}
	val paramsJson = lambdaParamsJson(ownParams)
	val resultType = birType(fn.returnType)
	// Enclosing generic type params referenced by the SM (captures/params/return/body operands) -> open SM
	// instantiation. BARE names: bir2cir prepends `gp:`.
	val freeTps = freeTypeParams(captures.map { it.type } + fn.parameters.map { it.type } + listOf(fn.returnType) + bodyTypeOperands(fn))
	// The SM's own generic-parameter NAME declarations (the enclosing free type params the state machine is
	// generic over) — a type-param DECLARATION list (bir2cir names the SM's params + instantiates `!i`), NOT a
	// type-USAGE slot, so it rides as the `typeParams` name shorthand (§2.5), consistent with the other lambda paths.
	val typeParamsBare = freeTps.joinToString(",") { str(it.name.asString()) }
	val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
	return """{"k":"newSuspendLambda","arity":${ownParams.size},"captures":[$capturesJson],"params":[$paramsJson],"suspendRet":${str(resultType)},"typeParams":[$typeParamsBare],"body":[$body],"funcType":${funcTypeOf(fn).toJson()}}"""
}

/** SHADOW the lambda's own regular params in `valSubst` while emitting its body: an enclosing lambda carrier
 *  may have bound the SAME name (e.g. `it`) to an outer local. A lifted lambda's params are its OWN method
 *  params — `IrGetValue` resolves them by NAME through `valSubst` (BirEmitterExpressions), so a stale outer
 *  binding would make the body reference a foreign local that is not in the lifted method's scope (`load unknown
 *  var` at ilemit). Removing them yields the correct bare `{"k":"local","name":<param>}`. Saved + restored,
 *  mirroring `emitInlineLambdaCarrier`. */
private inline fun <T> BirEmitter.withLambdaParamShadow(fn: IrSimpleFunction, block: () -> T): T {
	val names = fn.parameters.filter { it.kind == IrParameterKind.Regular }.map { it.name.asString() }
	val saved = names.associateWith { valSubst[it] }
	names.forEach { valSubst.remove(it) }
	try { return block() } finally {
		saved.forEach { (n, prev) -> if (prev != null) valSubst[n] = prev else valSubst.remove(n) }
	}
}

internal fun BirEmitter.lambda(node: IrFunctionExpression): String {
	val fn = node.function
	// A `suspend` lambda LITERAL -> a `newSuspendLambda` node: bir2cir turns it into a SuspendLambda state machine
	// (app-build only; the SM's create/resume protocol makes `blockOn { ... }` run). kotc emits only the pure FACTS
	// (captures/params/body-with-suspendCall-tags); the SM lowering is downstream. Any arity N flows through
	// suspendLambda now; restricted-suspension builders (sequence{}/iterator{}) go through it too — bir2cir gives
	// them the RestrictedSuspendLambda base.
	if (fn.isSuspend) suspendLambda(node)?.let { return it }
	// kotc does NO coroutine lowering: a `suspend () -> T` lambda emits as a PLAIN lambda (its suspend calls carry
	// `"suspendCall":true`); the Task-ABI / state-machine lowering is a deferred downstream layer. So the declared
	// return / delegate type stay the plain Kotlin shapes here.
	val ret = birType(fn.returnType)
	val ftype = funcTypeOf(fn)
	// A lambda has no `this` of its own, so a referenced `<this>` is the enclosing instance -> capture it.
	val captures = capturedVars(fn, includeThis = true)
	if (captures.isEmpty()) {
		val lname = "__lambda${lambdaCounter++}"
		val freeTps = freeTypeParams(fn.parameters.map { it.type } + listOf(fn.returnType) + bodyTypeOperands(fn))
		val typeParams = typeParamsJson(freeTps)
		run {
			val body = withLambdaParamShadow(fn) { (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) } }
			liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false$typeParams,"params":[${lambdaParamsJson(fn.parameters)}],"ret":${str(ret)},"body":[$body]}""")
		}
		val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
		return """{"k":"newDelegate","method":${str(lname)},"funcType":${str(ftype)}$typeArgs}"""
	}
	// Capturing: build a closure class. Captures rewrite to `this.<field>` (by symbol identity, so the
	// enclosing `this` — captured when the lambda reads a member — maps to a `__outer` field, not the
	// closure's own `this`). For a CPS suspend lambda the closure `invoke` is an INSTANCE coroutine; ilemit
	// captures the closure `this` into the state machine so resume can still read the captured-var fields.
	val cname = "dotkt\$${synthScope}\$Closure${closureCounter++}"
	val capPairs = captures.map { it to captureFieldName(it) }
	// Save any prior substitution for each captured decl so the OUTER binding (e.g. an intrinsic block's `c`
	// bound to the coroutine's own continuation) is restored after the body — not blown away — so the capture
	// VALUE (capValueExpr below) is still evaluated correctly in the enclosing context.
	val savedSubst = capPairs.associate { (decl, _) -> decl to captureSubst[decl] }
	capPairs.forEach { (decl, fname) ->
		captureSubst[decl] = """{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":${str(fname)}}"""
	}
	val body = withLambdaParamShadow(fn) { (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) } }
	capPairs.forEach { (decl, _) -> val prev = savedSubst[decl]; if (prev != null) captureSubst[decl] = prev else captureSubst.remove(decl) }
	val fields = capPairs.joinToString(",") { (decl, fname) -> """{"name":${str(fname)},"type":${str(captureFieldType(decl))}}""" }
	// The closure must be GENERIC over any enclosing type parameters it captures (reified CLR generics — a `gp:T`
	// field is unresolved otherwise). Declare them on the class and pass them as type arguments at `newClosure`.
	val freeTps = freeTypeParams(capPairs.map { it.first.type } + fn.parameters.map { it.type } + listOf(fn.returnType) + bodyTypeOperands(fn))
	// #52 (kotc-purity): kotc no longer SYNTHESIZES the closure CLASS (a CLR-representation type). It emits the raw
	// build-INGREDIENTS as a transient `synthClass` fact (capture fields=name+type, invoke params/ret/body, generic
	// type-param decls); bir2cir's ClosureSynthesis assembles the actual closure class (kind/base/interfaces wrapper +
	// the ctor field-init body) into the file `types`, then STRIPS `synthClass`, leaving the lean `newClosure`
	// (closureType + capture VALUE exprs + funcType + typeArgs) that ilemit consumes for the `new` — byte-identical.
	val synthClass = """{"name":${str(cname)},"fields":[$fields],"params":[${lambdaParamsJson(fn.parameters)}],"ret":${str(ret)},"body":[$body]${typeParamsJson(freeTps)}}"""
	// Capture values are evaluated in the enclosing context (the outer `this`, or an outer local).
	val capExprs = captures.joinToString(",") { capValueExpr(it) }
	val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
	return """{"k":"newClosure","closureType":${fqnJson(cname)},"captures":[$capExprs],"method":"invoke","funcType":${str(ftype)}$typeArgs,"synthClass":$synthClass}"""
}

/**
 * SAM conversion `Comparator { a, b -> … }` -> a synthetic class that IMPLEMENTS the fun interface (the SAM method =
 * the lambda body) and is instantiated via `newSam`. Unlike a function-type lambda (which lowers to a Func delegate),
 * a fun-interface value is used by INTERFACE (`comparator.compare(...)`), so a delegate has no matching method
 * (EntryPointNotFound). This mirrors the closure-class build but implements the iface + names the method after the SAM
 * + override:true, and returns the instance itself (not a delegate). Reuses the working object:Comparator emission.
 */
internal fun BirEmitter.samConversion(node: IrTypeOperatorCall): String {
	val funIface = node.typeOperand
	val ifaceClass = funIface.classifierOrNull?.owner as? IrClass ?: return expr(node.argument)
	val lamExpr = node.argument as? IrFunctionExpression ?: return expr(node.argument)   // fun-ref / existing impl -> fall back
	val fn = lamExpr.function
	val sam = ifaceClass.declarations.filterIsInstance<IrSimpleFunction>()
		.singleOrNull { it.modality == org.jetbrains.kotlin.descriptors.Modality.ABSTRACT } ?: return expr(node.argument)
	val samName = sam.name.asString()
	val ret = birType(fn.returnType)
	val captures = capturedVars(fn, includeThis = true)
	val cname = "dotkt\$${synthScope}\$Sam${closureCounter++}"
	val capPairs = captures.map { it to captureFieldName(it) }
	// (kotc reads NEITHER @ClrTypeAlias NOR @ClrIntrinsic — foundational invariant.) The stdlib no longer aliases any
	// `fun interface` to a NON-generic BCL interface (Comparator is a plain Kotlin fun interface), so there is no
	// object-param erasure / SAM-arg cast bridge to apply here; the SAM shim implements the Kotlin fun-interface
	// identity directly and bir2cir derives any CLR type off the ref.dll.
	val savedSubst = java.util.IdentityHashMap<IrValueDeclaration, String?>()
	capPairs.forEach { (decl, fname) -> savedSubst[decl] = captureSubst[decl]; captureSubst[decl] = """{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":${str(fname)}}""" }
	val samParams = lambdaParamsJson(fn.parameters)
	val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
	val samMethod = """{"name":${str(samName)},"static":false,"override":true,"virtual":true,"params":[$samParams],"ret":${str(ret)},"body":[$body]}"""
	savedSubst.forEach { (decl, prev) -> if (prev != null) captureSubst[decl] = prev else captureSubst.remove(decl) }
	val fields = capPairs.joinToString(",") { (decl, fname) -> """{"name":${str(fname)},"type":${str(captureFieldType(decl))}}""" }
	val ctorBody = capPairs.joinToString(",") { (_, fname) -> """{"k":"setField","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":${str(fname)},"value":{"k":"local","name":${str(fname)}}}""" }
	val ifaceSpec = ownerSpec(ifaceClass, funIface) ?: birType(funIface)
	val freeTps = freeTypeParams(listOf(funIface) + capPairs.map { it.first.type } + fn.parameters.map { it.type } + listOf(fn.returnType) + bodyTypeOperands(fn))
	// #52/#75: the SAM shim class travels as a `synthClass` FACT ON the `newSam` node — NOT `liftedTypes.add`'d as a
	// sibling type. A sibling type stays in the ORIGIN file; when this `newSam` rides in an inline fn's [KotlinInline]
	// payload and is spliced into a CONSUMING file (cross-module compareBy/comparator), only the fn body travels, so the
	// SAM class must be self-carried on the node. bir2cir's ClosureSynthesis assembles it into the consuming file (with
	// name-dedup) and strips `synthClass` — exactly as it does for `lambda()`'s `newClosure` synthClass. Same-file (no
	// splice): ClosureSynthesis synthesizes the SAME class into this file, so a non-spliced `newSam` is unchanged.
	val synthClass = """{"name":${str(cname)},"kind":"class","generated":true${typeParamsJson(freeTps)},"base":null,"interfaces":[${str(ifaceSpec)}],"fields":[$fields],"ctors":[{"params":[$fields],"baseArgs":null,"body":[$ctorBody]}],"methods":[$samMethod]}"""
	val capExprs = captures.joinToString(",") { capValueExpr(it) }
	val tArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
	return """{"k":"newSam","samType":${fqnJson(cname)},"captures":[$capExprs]$tArgs,"synthClass":$synthClass}"""
}

/**
 * A callable reference `::foo` -> a delegate bound to the referenced function. Handled: a top-level/static
 * function reference (no receiver — reuses the lambda `newDelegate` path over the static file-class method), a
 * constructor reference (`::Ctor`/`::NetType` -> a lifted static factory), a bound-instance reference
 * (`obj::method` -> `newBoundDelegate`), an UNBOUND member reference (`Class::method` -> a lifted static
 * `__mref(self, args)`), a .NET method reference (bound/unbound), and — G8 — an UNBOUND top-level
 * EXTENSION-function reference (`String::isNotBlank`, `Type::extFn`): a lifted static forwarder whose BODY is the
 * faithful extension call (the `callStatic owner:null` shape the direct top-level ext-call path emits in `call()`),
 * bound as a delegate. The forwarder is
 * needed (not a bare `ldftn`) because a @ClrIntrinsic stdlib ext has no real rt.dll body (bir2cir substitutes it
 * to a BCL call) — the CALL node gives bir2cir something to substitute. (Plain @InlineOnly funs without @ClrIntrinsic
 * DO get real rt.dll bodies; @ClrIntrinsic is the body-removing discriminator.) DEFERRED (clean `unsupported`, each
 * with its concrete blocker): a BOUND extension reference (`expr::extFn` — a closed static delegate is not
 * ilverify-clean), a suspend reference
 * (needs the coroutine SM), and a .NET-method deferral case.
 */
internal fun BirEmitter.functionRef(node: IrFunctionReference): String {
	// `::Ctor` (constructor reference) -> a lifted static factory `__ctorref_N(args) = new T(args)`, bound as a
	// delegate (delegates can't bind a ctor directly). `Func<…,UserType>` now resolves via DelegateCtor.
	(node.symbol.owner as? IrConstructor)?.let { ctor ->
		val klass = ctor.parent as? IrClass
		if (klass != null && !isExternalNetType(klass)) {
			val ps = ctor.parameters.filter { it.kind == IrParameterKind.Regular }
			val lname = "__ctorref${lambdaCounter++}"
			val psJson = ps.joinToString(",") { """{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}}""" }
			val argsJson = ps.joinToString(",") { """{"k":"local","name":${str(it.name.asString())}}""" }
			val retT = birType(ctor.returnType)
			val newE = """{"k":"new","type":${ownerSpec(klass, ctor.returnType).toJson()},"args":[$argsJson]}"""
			val freeTps = freeTypeParams(ps.map { it.type } + listOf(ctor.returnType))
			val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
			liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${str(retT)},"body":[{"k":"return","value":$newE}]}""")
			return """{"k":"newDelegate","method":${str(lname)},"funcType":${TypeNode.Fn(false, retT, ps.map { birTypeDeleg(it.type) }).toJson()}$typeArgs}"""
		}
		// `::NetType` — a lifted factory `__ctorref(args) = new NetType(args)`, bound as a delegate. kotc emits a
		// plain `new` carrying the .NET-FQN identity; bir2cir TransformNew reshapes it to `newClr` off the refs.
		if (klass != null) {
			val ps = ctor.parameters.filter { it.kind == IrParameterKind.Regular }
			val lname = "__ctorref${lambdaCounter++}"
			val psJson = ps.joinToString(",") { """{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}}""" }
			val argsJson = ps.joinToString(",") { """{"k":"local","name":${str(it.name.asString())}}""" }
			val retT = birType(ctor.returnType)
			val newE = """{"k":"new","type":${fqnJson(clrName(klass)!!)},"argTypes":[${ps.joinToString(",") { birType(it.type).toJson() }}],"args":[$argsJson]}"""
			val freeTps = freeTypeParams(ps.map { it.type } + listOf(ctor.returnType))
			val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
			liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${str(retT)},"body":[{"k":"return","value":$newE}]}""")
			return """{"k":"newDelegate","method":${str(lname)},"funcType":${TypeNode.Fn(false, retT, ps.map { birTypeDeleg(it.type) }).toJson()}$typeArgs}"""
		}
		return unsupported(node, "this constructor reference", "the constructor's class could not be resolved")
	}
	val fn = node.symbol.owner as? IrSimpleFunction
		?: return unsupported(node, "this function reference", "only references to plain (simple) functions are supported")
	val dispatchIdx = fn.parameters.indexOfFirst { it.kind == IrParameterKind.DispatchReceiver }
	val hasExt = fn.parameters.any { it.kind == IrParameterKind.ExtensionReceiver }
	// `::topLevelFun` — no receiver: a delegate over the static file-class method (FindStatic resolves it).
	if (dispatchIdx < 0 && !hasExt)
		return """{"k":"newDelegate","method":${str(fn.name.asString())},"funcType":${funcTypeOf(fn).toJson()}}"""
	// `Type::extFn` — an EXTENSION-function reference (G8). The delegate's target is a lifted static forwarder whose
	// BODY is the faithful extension CALL — the SAME shape a DIRECT call to this callee would emit (`owner:null` for a
	// stdlib/this-module ext, `ownerType:fileClass` for a referenced-assembly facade ext), NOT a bare `ldftn` of the
	// stdlib fn: a @ClrIntrinsic stdlib ext (`isNotBlank`, …) has no real rt.dll body (bir2cir substitutes it), so bir2cir needs a
	// CALL node to substitute. The forwarder params + the delegate funcType come from `birType(node.type)` — the
	// CALL-SITE-resolved `KFunctionN<P1..Pn,R>` (receiver first, then regulars), NOT `funcTypeOf(fn)`: a
	// `String::isNotBlank` reference resolves the receiver to `String` at the call site, though `isNotBlank` is
	// DECLARED on `CharSequence` — using the declared type would emit `Func<CharSequence,bool>` for an expected
	// `Func<string,bool>`.
	if (hasExt) {
		val extIdx = fn.parameters.indexOfFirst { it.kind == IrParameterKind.ExtensionReceiver }
		val boundExt = if (extIdx >= 0) node.arguments.getOrNull(extIdx) else null
		// A suspend ext reference (KSuspendFunctionN) needs the coroutine state-machine lowering — separate machinery.
		if (fn.isSuspend)
			return unsupported(node, "a suspend function reference",
				"a suspend callable reference needs the coroutine state-machine lowering (KSuspendFunction), not yet wired")
		// BOUND (`expr::extFn`): a closed static delegate over the ext forwarder is NOT ilverify-clean (ECMA-335 wants
		// `ldnull` for a static-method delegate target). Lift a CAPTURE CLASS — exactly a capturing lambda
		// `{ args -> expr.extFn(args) }`: a synth closure with a `__recv` field holding the receiver (evaluated ONCE,
		// eagerly, at reference-creation time), whose INSTANCE `invoke(args)` forwards to `extFn(__recv, args)`. Reuses
		// the `newClosure` path (bir2cir's ClosureSynthesis assembles the class; ilemit binds the delegate over the
		// instance `invoke` via `ldftn instance` + `newobj` — ilverify-clean, Codex-confirmed).
		// `dispatchIdx < 0` keeps this BELOW the member-extension guard: a (currently inexpressible) bound member ext
		// (dispatch + ext) must fall through to `unsupported`, not misroute to an `owner:null` forwarder ignoring dispatch.
		if (boundExt != null && dispatchIdx < 0)
			return boundExtFnRef(node, fn, boundExt)
		// A MEMBER extension (`class C { fun T.f() }`, dispatch + ext receiver) is not expressible as a callable
		// reference in Kotlin, so `dispatchIdx >= 0 && hasExt` cannot occur; deferred defensively.
		if (dispatchIdx >= 0)
			return unsupported(node, "a member extension-function reference",
				"a member extension function cannot be referenced as a callable in Kotlin")
		// UNBOUND top-level `fun T.f(args)` -> a lifted static `__mref(__self, __a1..) = f(__self, __a1..)`.
		val fnType = birType(node.type) as? TypeNode.Fn
			?: return unsupported(node, "this extension-function reference",
				"its inferred type was not a resolvable KFunction type")
		val selfT = fnType.params.firstOrNull()
			?: return unsupported(node, "this extension-function reference",
				"the reference's inferred type carries no receiver parameter")
		val regTypes = fnType.params.drop(1)
		val lname = "__mref${lambdaCounter++}"
		val psJson = (listOf("""{"name":"__self","type":${selfT.toJson()}}""") +
			regTypes.mapIndexed { i, t -> """{"name":${str("__a${i + 1}")},"type":${t.toJson()}}""" }).joinToString(",")
		val callArgs = (listOf("""{"k":"local","name":"__self"}""") +
			regTypes.indices.map { """{"k":"local","name":${str("__a${it + 1}")}}""" }).joinToString(",")
		// The reference's OWN instantiated type args (a generic ext `List<T>::foo` referenced as `List<Int>::foo`) ->
		// the inner call's `typeArgs` so bir2cir/ilemit MakeGenericMethod. Empty for `isNotBlank`/`indentWidth`.
		val refTps = fn.typeParameters
		val refTaArgs = refTps.indices.map { node.typeArguments.getOrNull(it) }
		val hasRefTa = refTps.isNotEmpty() && refTaArgs.all { it != null }
		val refTa = if (!hasRefTa) "" else ""","typeArgs":[${refTaArgs.joinToString(",") { birType(it!!).toJson() }}]"""
		val retT = fnType.ret
		val retVoid = retT == TypeNode.Fqn("kotlin.Unit")   // the SUBSTITUTED return (fn's own T may resolve to Unit)
		// The inner call MUST mirror the DIRECT top-level ext-call shape for this same callee, so bir2cir attributes
		// the forwarded call identically to a direct one. A callee restored from a referenced-assembly [KotlinFile]
		// facade (`body == null` + an injected file class) emits the `ownerType:fileClass` identity (mirrors the
		// injected-facade ext-call gate in `call()`); every other top-level ext (stdlib-from-klib, this-module) emits
		// the plain `owner:null` shape (the direct top-level ext-call `callStatic owner:null` in `call()`).
		val injectedFileClass = if (fn.body == null)
			(fn.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)?.let {
				kotc.frontend.clrInjectedTopLevelFileClass(CallableId(it.packageFqName, fn.name), regularParams(fn).size)
			} else null
		val callE = if (injectedFileClass != null) {
			// `__self` = the ext receiver, so it heads both the args and the shape/argTypes (matches the injected
			// top-level ext-call branch in `call()`; declared param types are used for the facade signature lookup).
			val extRecvParam = fn.parameters.first { it.kind == IrParameterKind.ExtensionReceiver }
			val declShapeTypes = (listOf(extRecvParam) + regularParams(fn)).joinToString(",") { birType(it.type).toJson() }
			if (hasRefTa)
				"""{"k":"callStatic","ownerType":${str(injectedFileClass)},"method":${str(fn.name.asString())}$refTa,"shapeTypes":[$declShapeTypes],"args":[$callArgs]}"""
			else
				"""{"k":"callStatic","ownerType":${str(injectedFileClass)},"method":${str(fn.name.asString())},"argTypes":[$declShapeTypes],"ret":${retT.toJson()},"args":[$callArgs]}"""
		} else {
			"""{"k":"callStatic","owner":null,"method":${str(fn.name.asString())}${overloadSigField(fn)}$refTa${retHintStr(hasRefTa, retT)},"args":[$callArgs]}"""
		}
		val body = if (retVoid) """{"k":"exprStmt","expr":$callE}""" else """{"k":"return","value":$callE}"""
		// freeTypeParams over node.type's SUBSTITUTED args (not the declared fn params — same call-site-type trap):
		// picks up only genuine ENCLOSING-context type vars (a `fun <E> …` scope), never the ext fn's OWN T (already
		// substituted away in node.type). The lifted static must be generic over those enclosing vars.
		val nodeTypeArgs = (node.type as? IrSimpleType)?.arguments?.mapNotNull { (it as? IrTypeProjection)?.type }.orEmpty()
		val freeTps = freeTypeParams(nodeTypeArgs)
		val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
		liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${retT.toJson()},"body":[$body]}""")
		return """{"k":"newDelegate","method":${str(lname)},"funcType":${fnType.toJson()}$typeArgs}"""
	}
	// `obj::method` — a bound instance reference: a delegate whose target is the bound receiver. Only USER
	// classes (the method resolves via FindMethod); .NET-method / extension / unbound refs are deferred.
	val boundRecv = if (dispatchIdx >= 0 && !hasExt) node.arguments.getOrNull(dispatchIdx) else null
	val ownerClass = fn.parent as? IrClass
	if (boundRecv != null && ownerClass != null && !isExternalNetType(ownerClass)) {
		val virtual = fn.modality != Modality.FINAL || fn.overriddenSymbols.isNotEmpty()
		return """{"k":"newBoundDelegate","ownerType":${fqnJson(typeName(ownerClass))},"method":${str(fn.name.asString())},"virtual":$virtual,"recv":${expr(boundRecv)},"funcType":${funcTypeOf(fn).toJson()}${if (isAnySlotMethod(fn)) ""","anySlot":true""" else ""}}"""
	}
	// `Class::method` (UNbound) -> a lifted static `__mref(self, args) = self.method(args)`; the receiver
	// becomes the delegate's first parameter. User classes only (`Func<UserType,…>` resolves via DelegateCtor).
	if (dispatchIdx >= 0 && boundRecv == null && !hasExt && ownerClass != null && !isExternalNetType(ownerClass)) {
		val selfT = birType(fn.parameters[dispatchIdx].type)
		val ps = fn.parameters.filter { it.kind == IrParameterKind.Regular }
		val lname = "__mref${lambdaCounter++}"
		val psJson = (listOf("""{"name":"__self","type":${str(selfT)}}""") +
			ps.map { """{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}}""" }).joinToString(",")
		val argsJson = ps.joinToString(",") { """{"k":"local","name":${str(it.name.asString())}}""" }
		val virtual = fn.modality != Modality.FINAL || fn.overriddenSymbols.isNotEmpty()
		val callE = """{"k":"callInstance","ownerType":${fqnJson(typeName(ownerClass))},"virtual":$virtual,"recv":{"k":"local","name":"__self"},"method":${str(fn.name.asString())},"args":[$argsJson]${if (isAnySlotMethod(fn)) ""","anySlot":true""" else ""}}"""
		val retVoid = fn.returnType.isUnit()
		val retT = birType(fn.returnType)
		val body = if (retVoid) """{"k":"exprStmt","expr":$callE}""" else """{"k":"return","value":$callE}"""
		val freeTps = freeTypeParams(listOf(fn.parameters[dispatchIdx].type) + ps.map { it.type } + listOf(fn.returnType))
		val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
		liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${str(retT)},"body":[$body]}""")
		return """{"k":"newDelegate","method":${str(lname)},"funcType":${TypeNode.Fn(false, retT, listOf(selfT) + ps.map { birTypeDeleg(it.type) }).toJson()}$typeArgs}"""
	}
	// A .NET method reference. Bound `obj::m` -> a NEUTRAL `newBoundDelegate` carrying the owner identity; bir2cir
	// shapes it to the CLR bound delegate. Unbound `NetType::m` -> a lifted static `__mref(self, args) = self.m(args)`.
	val clrOwner = ownerClass?.let { clrName(it) }
	if (clrOwner != null && !hasExt) {
		val regs = fn.parameters.filter { it.kind == IrParameterKind.Regular }
		val argTypes = regs.joinToString(",") { birType(it.type).toJson() }
		val member = fn.name.asString()
		val anySlotTag = if (isAnySlotMethod(fn)) ""","anySlot":true""" else ""
		val virtual = fn.modality == Modality.OPEN || fn.modality == Modality.ABSTRACT
		if (boundRecv != null)
			return """{"k":"newBoundDelegate","ownerType":${fqnJson(clrOwner)},"method":${str(member)},"argTypes":[$argTypes],"virtual":$virtual,"recv":${expr(boundRecv)},"funcType":${funcTypeOf(fn).toJson()}$anySlotTag}"""
		if (dispatchIdx >= 0) {
			val selfT = birType(fn.parameters[dispatchIdx].type)
			val lname = "__mref${lambdaCounter++}"
			val psJson = (listOf("""{"name":"__self","type":${str(selfT)}}""") +
				regs.map { """{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}}""" }).joinToString(",")
			val argsJson = regs.joinToString(",") { """{"k":"local","name":${str(it.name.asString())}}""" }
			val retVoid = fn.returnType.isUnit()
			val retT = birType(fn.returnType)
			// A genuine `NetType::m` method reference -> a lifted static forwarding to the .NET instance method.
			// (A kotlin.collections `Iterable::iterator` never reaches here: clrOwner is null for a jar-sourced stdlib
			// collection interface, so the enumerator-bridge routing lives in bir2cir Rule 5, not this clrOwner!=null path.)
			val callE = """{"k":"callInstance","ownerType":${str(clrOwner)},"method":${str(member)},"argTypes":[$argTypes],"ret":${birType(fn.returnType).toJson()},"recv":{"k":"local","name":"__self"},"args":[$argsJson]$anySlotTag}"""
			val body = if (retVoid) """{"k":"exprStmt","expr":$callE}""" else """{"k":"return","value":$callE}"""
			val freeTps = freeTypeParams(listOf(fn.parameters[dispatchIdx].type) + regs.map { it.type } + listOf(fn.returnType))
			val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
			liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${str(retT)},"body":[$body]}""")
			return """{"k":"newDelegate","method":${str(lname)},"funcType":${TypeNode.Fn(false, retT, listOf(selfT) + regs.map { birTypeDeleg(it.type) }).toJson()}$typeArgs}"""
		}
	}
	return unsupported(node, "a method reference to a .NET method (`::${fn.name}`)",
		"wrap the call in a lambda instead, e.g. `{ a -> x.${fn.name}(a) }`")
}

/**
 * A BOUND extension-function reference `expr::extFn` -> a CAPTURE-CLASS lift, identical to a capturing lambda
 * `{ args -> expr.extFn(args) }`. A closed static delegate over the ext forwarder is not ilverify-clean (ECMA-335 wants
 * `ldnull` as a static-method delegate target), so we synthesize a closure with a single `__recv` field holding the
 * eagerly-evaluated receiver, whose INSTANCE `invoke(args)` forwards to `extFn(__recv, args)` — the SAME `newClosure`
 * shape `lambda()` emits for a capturing lambda (bir2cir's ClosureSynthesis assembles the class; ilemit binds the
 * delegate over the instance method: `ldftn instance invoke` + `newobj Func` — verifiable). The forwarding CALL mirrors
 * the UNBOUND ext-forwarder's inner call exactly (owner:null stdlib/this-module ext, or ownerType:fileClass for a
 * referenced-assembly facade ext), so bir2cir attributes it identically to a direct top-level ext call.
 */
internal fun BirEmitter.boundExtFnRef(node: IrFunctionReference, fn: IrSimpleFunction, boundExt: IrExpression): String {
	val fnType = birType(node.type) as? TypeNode.Fn
		?: return unsupported(node, "this bound extension-function reference",
			"its inferred type was not a resolvable KFunction type")
	// The bound type carries only the REMAINING args (the receiver is already bound), so `fnType.params` = the delegate's
	// params = the `invoke` method's params. The captured receiver's field type comes from the receiver expression.
	val selfT = birType(boundExt.type)
	val regTypes = fnType.params
	val retT = fnType.ret
	val retVoid = retT == TypeNode.Fqn("kotlin.Unit")
	val cname = "dotkt\$${synthScope}\$Closure${closureCounter++}"
	val invokeParams = regTypes.mapIndexed { i, t -> """{"name":${str("__a${i + 1}")},"type":${t.toJson()}}""" }.joinToString(",")
	val recvArg = """{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":"__recv"}"""
	val callArgs = (listOf(recvArg) + regTypes.indices.map { """{"k":"local","name":${str("__a${it + 1}")}}""" }).joinToString(",")
	// The reference's OWN instantiated type args (a generic ext referenced as `expr::foo<Int>`) -> the inner call's
	// `typeArgs` so bir2cir/ilemit MakeGenericMethod (mirrors the unbound branch).
	val refTps = fn.typeParameters
	val refTaArgs = refTps.indices.map { node.typeArguments.getOrNull(it) }
	val hasRefTa = refTps.isNotEmpty() && refTaArgs.all { it != null }
	val refTa = if (!hasRefTa) "" else ""","typeArgs":[${refTaArgs.joinToString(",") { birType(it!!).toJson() }}]"""
	// The forwarding call MUST mirror the DIRECT top-level ext-call shape for this same callee (see the UNBOUND branch):
	// a referenced-assembly facade ext (body==null + injected file class) emits `ownerType:fileClass`; every other
	// top-level ext emits the plain `owner:null` shape. Only the receiver arg differs (`this.__recv` vs `__self`).
	val injectedFileClass = if (fn.body == null)
		(fn.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)?.let {
			kotc.frontend.clrInjectedTopLevelFileClass(CallableId(it.packageFqName, fn.name), regularParams(fn).size)
		} else null
	val callE = if (injectedFileClass != null) {
		val extRecvParam = fn.parameters.first { it.kind == IrParameterKind.ExtensionReceiver }
		val declShapeTypes = (listOf(extRecvParam) + regularParams(fn)).joinToString(",") { birType(it.type).toJson() }
		if (hasRefTa)
			"""{"k":"callStatic","ownerType":${str(injectedFileClass)},"method":${str(fn.name.asString())}$refTa,"shapeTypes":[$declShapeTypes],"args":[$callArgs]}"""
		else
			"""{"k":"callStatic","ownerType":${str(injectedFileClass)},"method":${str(fn.name.asString())},"argTypes":[$declShapeTypes],"ret":${retT.toJson()},"args":[$callArgs]}"""
	} else {
		"""{"k":"callStatic","owner":null,"method":${str(fn.name.asString())}${overloadSigField(fn)}$refTa${retHintStr(hasRefTa, retT)},"args":[$callArgs]}"""
	}
	val forwardBody = if (retVoid) """{"k":"exprStmt","expr":$callE}""" else """{"k":"return","value":$callE}"""
	// The closure must be GENERIC over any enclosing type params referenced by the captured receiver or the invoke
	// signature (reified CLR generics) — mirrors `lambda()`'s freeTps over capture+param+ret types.
	val nodeTypeArgs = (node.type as? IrSimpleType)?.arguments?.mapNotNull { (it as? IrTypeProjection)?.type }.orEmpty()
	val freeTps = freeTypeParams(listOf(boundExt.type) + nodeTypeArgs)
	val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
	val synthClass = """{"name":${str(cname)},"fields":[{"name":"__recv","type":${str(selfT)}}],"params":[$invokeParams],"ret":${str(retT)},"body":[$forwardBody]${typeParamsJson(freeTps)}}"""
	return """{"k":"newClosure","closureType":${fqnJson(cname)},"captures":[${expr(boundExt)}],"method":"invoke","funcType":${str(fnType)}$typeArgs,"synthClass":$synthClass}"""
}

/**
 * The materialization of a delegate accessor's compiler-synthesized `KProperty` argument (a `getValue`/
 * `setValue`/`provideDelegate` 2nd arg, IR origin `PROPERTY_REFERENCE_FOR_DELEGATE`) -> `new
 * kotlin.reflect.ClrPropertyStub(name)`, the REAL emitted stdlib name-only impl of `KProperty<Any?>`
 * (KCallable.name + KAnnotatedElement.annotations). Kotlin's own delegate convention only ever reads `.name`
 * off this argument in ordinary bodies — never get()/set()/invoke() — so this cheap stub (not the full
 * `propertyRef` lift below) is always correct for it. Replaces the retired `dotkt$KPropertyImpl` synthetic.
 */
internal fun BirEmitter.kPropertyStub(name: String): String =
	"""{"k":"new","type":${TypeNode.Fqn("kotlin.reflect.ClrPropertyStub", listOf(OBJ)).toJson()},"args":[{"k":"const","type":${fqnJson("kotlin.String")},"value":${str(name)}}]}"""

/**
 * A genuine callable reference to a property (`::x`, `obj::p`, `Type::p`) -> a lifted class implementing the
 * REAL emitted stdlib `kotlin.reflect.KProperty0`/`KMutableProperty0`/`KProperty1`/`KMutableProperty1<…>`
 * interface (mirrors `samConversion`: a KProperty interface has no faithful .NET delegate representation, so
 * this is a lifted CLASS via `new`, not a `newDelegate`). `node.type` already carries FIR's resolved interface
 * identity + its (possibly generic) V/T arguments — reused verbatim as both the `interfaces` entry and the
 * get/set param types, so a captured enclosing generic (`Box<T>::value`) resolves through the SAME `birType`/
 * `typeArgSubst` machinery any other reference does (no separate remap here).
 *
 * v1 scope: a TOP-LEVEL property (`::x`), or a MEMBER property either BOUND (`obj::p`, receiver captured in a
 * field) or UNBOUND (`Type::p`, receiver becomes the `get`/`set`'s own leading param) — mirrors `functionRef`'s
 * ctor-ref/bound/unbound split. An EXTENSION-receiver property reference (`KProperty2`), a `lateinit var`, a
 * `@ClrField` property, and a property overriding a .NET-mapped interface member (kotlin.CharSequence.length) are
 * clean deferrals (their access shape differs from the plain get_/set_ accessor convention used below). The
 * compiler-synthesized KProperty argument of a delegate's
 * getValue/setValue/provideDelegate is NOT this path — those call sites materialize `kPropertyStub` directly
 * without going through `expr()`/this dispatch; the origin check below is a defensive fallback only.
 */
internal fun BirEmitter.propertyRef(node: IrPropertyReference): String {
	if (node.origin == IrStatementOrigin.PROPERTY_REFERENCE_FOR_DELEGATE)
		return kPropertyStub(node.symbol.owner.name.asString())
	// An extension-receiver property reference (`String::someExtProp`, KProperty2) has no supported lowering yet.
	// Test the accessor's PARAMETER SHAPE, not the bound ARGUMENT: a bound-argument test is null for an UNBOUND
	// top-level ext-property ref (no receiver argument present), so it would slip past this guard and emit a
	// 0-arg `callStatic get_<name>` for an interface slot that actually needs `get(receiver)` (a param-count-
	// mismatched override -> TypeLoad/miscompile). The getter/setter parameter list carries the ext receiver
	// regardless of whether the reference is bound.
	val extAccessor = node.getter?.owner ?: node.setter?.owner
	if (extAccessor?.parameters?.any { it.kind == IrParameterKind.ExtensionReceiver } == true)
		return unsupported(node, "this property reference",
			"an extension-receiver property reference (KProperty2) has no supported lowering yet")
	val prop = node.symbol.owner
	if (prop.isLateinit || isClrField(prop))
		return unsupported(node, "this property reference",
			"a lateinit/@ClrField property reference has no supported lowering yet")
	val getterFn = node.getter?.owner ?: prop.getter
		?: return unsupported(node, "this property reference", "the referenced property has no getter")
	// A reference to a property DIRECTLY overriding kotlin.CharSequence.length is a clean deferral: its accessor
	// binds the .NET-mapped interface slot (get_length routes to System.String's get_Length via bir2cir), whose
	// lift-through the plain get_/set_ accessor convention below cannot faithfully name — the blocker is the
	// interface-slot rename, owned by bir2cir, not a shape kotc can emit here. Walks only the direct override
	// chain (non-transitive, matching the retired clrIfaceMemberName helper): a `class B : A` where
	// `A : CharSequence` re-overrides through A, so `B::length` takes the plain lift.
	val overridesCharSeqLength = (sequenceOf(getterFn) + getterFn.overriddenSymbols.asSequence().map { it.owner }).any { owner ->
		(owner.parent as? IrClass)?.fqNameWhenAvailable?.asString() == "kotlin.CharSequence"
			&& owner.correspondingPropertySymbol?.owner?.name?.asString() == "length"
	}
	if (overridesCharSeqLength)
		return unsupported(node, "this property reference",
			"a property overriding a .NET-mapped interface member has no supported lowering yet")
	val setterFn = if (prop.isVar) (node.setter?.owner ?: prop.setter) else null
	val declClass = getterFn.parent as? IrClass
	val name = prop.name.asString()
	val boundRecv = propRefDispatchReceiver(node)

	val ifaceSpec = birType(node.type) as? TypeNode.Fqn
		?: return unsupported(node, "this property reference",
			"its inferred type was not a KProperty/KMutableProperty interface")
	val ifaceArgs = ifaceSpec.args.orEmpty()
	val arity0 = ifaceSpec.name == "kotlin.reflect.KProperty0" || ifaceSpec.name == "kotlin.reflect.KMutableProperty0"
	val vType = ifaceArgs.lastOrNull() ?: OBJ
	val recvTypeNode = ifaceArgs.getOrNull(0).takeIf { !arity0 }   // KProperty1/KMutableProperty1's T (unbound only)

	val bound = declClass != null && arity0 && boundRecv != null
	val unbound = declClass != null && !arity0
	val cname = "dotkt\$${synthScope}\$PropRef${closureCounter++}"

	fun recvExprIn(): String = when {
		bound -> """{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":"__recv"}"""
		unbound -> """{"k":"local","name":"receiver"}"""
		else -> """{"k":"this"}"""
	}
	val memberOwner: TypeNode = when {
		bound -> ownerSpec(declClass, boundRecv!!.type)
		unbound -> recvTypeNode ?: OBJ
		else -> OBJ
	}
	fun accessorCall(isSetter: Boolean, extraArg: String?): String {
		val fn = if (isSetter) setterFn!! else getterFn
		val virtual = fn.modality != Modality.FINAL || fn.overriddenSymbols.isNotEmpty()
		val args = listOfNotNull(extraArg).joinToString(",")
		val method = str((if (isSetter) "set_" else "get_") + name)
		return """{"k":"callInstance","ownerType":${memberOwner.toJson()},"virtual":$virtual,"recv":${recvExprIn()},"method":$method,"args":[$args]}"""
	}

	val readBody: String = if (declClass == null) {
		// Top-level property: mirrors the ordinary top-level property-read path (a plain val/var is a static
		// field; a computed one — no backing field — is a get_<name>() static).
		val owner = fileClassOf(prop)
		if (prop.backingField == null)
			"""{"k":"return","value":{"k":"callStatic","owner":${fqnJson(owner)},"method":${str("get_$name")},"args":[]}}"""
		else """{"k":"return","value":{"k":"staticField","ownerType":${fqnJson(owner)},"name":${str(name)}}}"""
	} else """{"k":"return","value":${accessorCall(false, null)}}"""
	val readParams = if (unbound) """{"name":"receiver","type":${str(recvTypeNode ?: OBJ)}}""" else ""
	val getMethod = """{"name":"get","static":false,"override":true,"virtual":true,"params":[$readParams],"ret":${str(vType)},"body":[$readBody]}"""
	// KProperty0/KProperty1's declared supertype `() -> V`/`(T) -> V` gives them a REAL fake-overridden `invoke`
	// abstract member (confirmed in the compiled BIR: `interfaces` drops the FunctionN supertype — a Kotlin
	// function type has no faithful CLR interface base — but the interface's OWN `methods` still carries the
	// fake override AS ITS OWN abstract slot). So the lifted class must implement it too, same body as `get`
	// (mirrors JVM's `PropertyReferenceImpl.invoke() = get()`).
	val invokeMethod = """{"name":"invoke","static":false,"override":true,"virtual":true,"params":[$readParams],"ret":${str(vType)},"body":[$readBody]}"""

	val setMethod: String? = setterFn?.let {
		val setBody = if (declClass == null) {
			val owner = fileClassOf(prop)
			if (prop.backingField == null)
				"""{"k":"exprStmt","expr":{"k":"callStatic","owner":${fqnJson(owner)},"method":${str("set_$name")},"args":[{"k":"local","name":"value"}]}}"""
			else """{"k":"exprStmt","expr":{"k":"staticFieldSet","ownerType":${fqnJson(owner)},"name":${str(name)},"value":{"k":"local","name":"value"}}}"""
		} else """{"k":"exprStmt","expr":${accessorCall(true, """{"k":"local","name":"value"}""")}}"""
		val setParams = (if (unbound) """{"name":"receiver","type":${str(recvTypeNode ?: OBJ)}},""" else "") +
			"""{"name":"value","type":${str(vType)}}"""
		"""{"name":"set","static":false,"override":true,"virtual":true,"params":[$setParams],"ret":${str(TypeNode.Fqn("kotlin.Unit"))},"body":[$setBody]}"""
	}

	// KCallable.name + KAnnotatedElement.annotations are NOT re-synthesized here: the lifted class extends the real
	// stdlib `kotlin.reflect.ClrPropertyStub<V>(name)` (which provides `name` + `annotations get()=emptyList()`),
	// so it only implements the KProperty0/1 slots ClrPropertyStub lacks (get/set/invoke). Consolidates onto the
	// stdlib impl instead of duplicating a bare-name `emptyList` call. The lift already implements KProperty<V> via
	// its KProperty0/1 interface, so the ClrPropertyStub<V>:KProperty<V> base is a CLR-legal diamond.
	val stubBase = TypeNode.Fqn("kotlin.reflect.ClrPropertyStub", listOf(vType))
	val stubBaseArg = """{"k":"const","type":${fqnJson("kotlin.String")},"value":${str(name)}}"""

	val recvFieldType = if (bound) birType(boundRecv!!.type) else null
	val fields = if (bound) """{"name":"__recv","type":${str(recvFieldType!!)}}""" else ""
	val ctorParams = if (bound) """{"name":"__recv","type":${str(recvFieldType!!)}}""" else ""
	val ctorBody = if (bound) """{"k":"setField","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":"__recv","value":{"k":"local","name":"__recv"}}""" else ""

	val freeTps = freeTypeParams(listOf(node.type) + listOfNotNull(boundRecv?.type))
	val methods = listOfNotNull(getMethod, invokeMethod, setMethod).joinToString(",")
	liftedTypes.add("""{"name":${str(cname)},"kind":"class","generated":true${typeParamsJson(freeTps)},"base":${stubBase.toJson()},"interfaces":[${ifaceSpec.toJson()}],"fields":[$fields],"ctors":[{"params":[$ctorParams],"baseArgs":[$stubBaseArg],"body":[$ctorBody]}],"methods":[$methods]}""")

	val classType = if (freeTps.isEmpty()) TypeNode.Fqn(cname) else TypeNode.Fqn(cname, freeTps.map { tvOf(it) })
	val ctorArgs = if (bound) expr(boundRecv!!) else ""
	return """{"k":"new","type":${classType.toJson()},"args":[$ctorArgs]}"""
}

/** Free value references in a lambda body (referenced but not declared inside) = its captured vars. */
internal fun BirEmitter.capturedVars(fn: IrSimpleFunction, includeThis: Boolean = false): List<IrValueDeclaration> {
	val declared = HashSet<IrValueDeclaration>()
	fn.parameters.forEach { declared.add(it) }
	val referenced = LinkedHashSet<IrValueDeclaration>()
	fn.body?.acceptVoid(object : IrVisitorVoid() {
		override fun visitElement(element: IrElement) {
			when (element) {
				is IrVariable -> declared.add(element)
				// A nested lambda/local-fun's own parameters are declared there, not captured by `fn`.
				is IrValueParameter -> declared.add(element)
				is IrGetValue -> referenced.add(element.symbol.owner)
				is IrSetValue -> referenced.add(element.symbol.owner)
			}
			element.acceptChildrenVoid(this)
		}
	})
	return referenced.filter { it !in declared && (includeThis || it.name.asString() != "<this>") }
}

/**
 * Free outer values captured by an object literal: any value referenced anywhere in the anon class
 * (method bodies + property initializers) but declared OUTSIDE it. The anon's own receivers/params/locals
 * are excluded by identity — crucially this keeps the captured enclosing `this` (same name "<this>" as
 * the anon's own receiver, distinguished only by symbol identity).
 */
internal fun BirEmitter.capturedVarsForObject(anon: IrClass): List<IrValueDeclaration> {
	val own = java.util.Collections.newSetFromMap(java.util.IdentityHashMap<IrValueDeclaration, Boolean>())
	val referenced = LinkedHashSet<IrValueDeclaration>()
	anon.acceptChildrenVoid(object : IrVisitorVoid() {
		override fun visitElement(element: IrElement) {
			when (element) {
				is IrValueParameter -> own.add(element)
				is IrVariable -> own.add(element)
				is IrGetValue -> referenced.add(element.symbol.owner)
				is IrSetValue -> referenced.add(element.symbol.owner)
			}
			element.acceptChildrenVoid(this)
		}
	})
	return referenced.filter { it !in own }
}

/** Value declarations assigned (IrSetValue) anywhere inside an object literal (for mutable-capture detection). */
internal fun BirEmitter.mutatedIn(node: IrElement): Set<IrValueDeclaration> {
	val out = java.util.Collections.newSetFromMap(java.util.IdentityHashMap<IrValueDeclaration, Boolean>())
	node.acceptChildrenVoid(object : IrVisitorVoid() {
		override fun visitElement(element: IrElement) {
			if (element is IrSetValue) out.add(element.symbol.owner)
			element.acceptChildrenVoid(this)
		}
	})
	return out
}

/** A capture's field name: the enclosing `this` -> `__outer`, an outer local/param -> its own name. */
internal fun BirEmitter.captureFieldName(d: IrValueDeclaration): String =
	if (d.name.asString() == "<this>") "__outer" else d.name.asString()

/** A capture's value at the `new` site (in the enclosing context): the outer `this`, or an outer local. */
internal fun BirEmitter.capValueExpr(d: IrValueDeclaration): String =
	// Evaluate the capture VALUE in the enclosing context, mirroring `exprInner`'s IrGetValue resolution ORDER:
	// captureSubst (an outer closure field / intrinsic-block binding), then `selfSubst` (an EXTENSION receiver bound to
	// its `__self`/`__recv` — an inline-lambda carrier RENAMES its ext receiver via selfSubst, so a lifted local fn /
	// nested closure that captures that receiver must pick up the SAME renamed local, NOT the raw `$this$<fn>` IR name;
	// without this a `buildString { … appendTwoDigits(x) … }` carrier passed `$this$buildString` verbatim while its
	// param was `__recvN` -> ilemit "load unknown var $this$buildString"), then `valSubst`, then the `<this>`/local
	// fallback. selfSubst/captureSubst are IDENTITY-keyed (the receiver by symbol, not name).
	captureSubst[d] ?: selfSubst[d] ?: valSubst[d.name.asString()]
		?: if (d.name.asString() == "<this>") """{"k":"this"}""" else """{"k":"local","name":${str(d.name.asString())}}"""

/**
 * The lambda's value parameters in delegate order: the EXTENSION RECEIVER first (a receiver lambda
 * `Scope.() -> Unit` is `Function1<Scope, Unit>`, so its receiver is the first delegate argument — and the body's
 * implicit-receiver references resolve to it), then the regular params. Keeping this consistent with `birType`'s
 * view of the function type (which derives args from the FunctionN type arguments, receiver included) is what
 * makes `build { ... }` receiver-lambda DSLs work (feedback item 7).
 */
internal fun BirEmitter.orderedLambdaParams(fn: IrSimpleFunction): List<IrValueParameter> =
	fn.parameters.filter { it.kind == IrParameterKind.ExtensionReceiver } +
		fn.parameters.filter { it.kind == IrParameterKind.Regular }

/** The function type `fn` for a lambda's signature (extension receiver first). A `suspend` lambda sets
 *  `fn.suspend=true` — same delegate shape carrying the suspend FACT for the newSuspendLambda SM builder.
 *  bir2cir ERASES a suspend `fn` to `object` wherever it appears in a TYPE slot; only the `funcType` node
 *  key itself keeps it. So this stays behavior-preserving. */
internal fun BirEmitter.funcTypeOf(fn: IrSimpleFunction): TypeNode.Fn {
	val ps = orderedLambdaParams(fn).map { birTypeDeleg(it.type) }
	return TypeNode.Fn(fn.isSuspend, funcRetTypeOf(fn.returnType), ps)
}

/**
 * A function type's RETURN, preserving generic-parameter nullability: a `(T) -> R?` slot emits `nullable(tv)`
 * (the Kotlin FACT that the func's return is nullable — otherwise LOST for an unconstrained generic). bir2cir
 * CONSUMES the marker (a nullable-marked func return lowers to `object`, the erased CLR rep).
 */
internal fun BirEmitter.funcRetTypeOf(t: IrType): TypeNode {
	if (t.isUnit()) return TypeNode.Fqn("kotlin.Unit")
	// birTypeDeleg already wraps a nullable core as `{t:nullable,of:...}` (uniform birType), incl. a nullable `tv`.
	return birTypeDeleg(t)
}

/**
 * Like `birType`, for a delegate (Func/Action) signature slot. `KProperty*` is NO LONGER erased to Any here
 * (#70): it is a REAL emitted stdlib interface now (KPropertyClr.kt), not a `dotkt$KProperty` synthetic
 * TypeBuilder, so a `Delegates.observable`/`vetoable` callback's `(KProperty<*>, T, T) -> Unit` param carries
 * its real generic identity like any other stdlib interface in a Func/Action slot.
 */
internal fun BirEmitter.birTypeDeleg(t: IrType): TypeNode {
	// A Unit PARAM must be the real Unit VALUE identity, not `void` (a void param is invalid metadata); the RETURN
	// context special-cases Unit before calling this. The @/referenced-Unit decision is now bir2cir's.
	if (t.isUnit()) return TypeNode.Fqn("kotlin.Unit")
	return birType(t)
}

/** Lambda/closure method params with KProperty erased to Any (must agree with funcTypeOf for delegates):
 *  extension receiver first (so a receiver lambda's `$this$build` is bound), then regular params. */
internal fun BirEmitter.lambdaParamsJson(params: List<IrValueParameter>): String =
	(params.filter { it.kind == IrParameterKind.ExtensionReceiver } + params.filter { it.kind == IrParameterKind.Regular })
		// A `Unit`-typed PARAMETER must be the real Unit VALUE identity, not `void` (invalid metadata).
		.joinToString(",") { p ->
			val ty = if (p.type.isUnit()) TypeNode.Fqn("kotlin.Unit") else birTypeDeleg(p.type)
			"""{"name":${str(p.name.asString())},"type":${ty.toJson()}}"""
		}

/** Lift a local function to a file-class static method; captured vars become leading params (by their own names). */
internal fun BirEmitter.liftLocalFn(fn: IrSimpleFunction) {
	// Captured vars (incl. the enclosing `this`) become leading params; the call site prepends their values.
	val captures = capturedVars(fn, includeThis = true)
	val lname = "__local${scopeCounter++}_${fn.name.asString()}"
	// A local fn referencing an enclosing type parameter (in a capture, its own params, or its return) becomes a
	// GENERIC static method — reified CLR generics, same as a capturing closure class. The call site (callStatic)
	// passes the enclosing type params as type arguments.
	val ownRegParams = fn.parameters.filter { it.kind == IrParameterKind.Regular }
	val freeTps = freeTypeParams(captures.map { it.type } + ownRegParams.map { it.type } + listOf(fn.returnType))
	localFns[fn] = Triple(lname, captures, freeTps)
	fun pj(name: String, t: IrType) = """{"name":${str(name)},"type":${birType(t).toJson()}}"""
	val capPairs = captures.map { it to captureFieldName(it) }
	// Captures arrive as leading params; rewrite body refs to those params. This must cover not only `<this>` but
	// also receiver-like captured params such as `$this$buildString`, otherwise an active inline substitution can
	// leak a caller-local (`__lam<N>`) into the lifted method body.
	capPairs.forEach { (decl, fname) -> captureSubst[decl] = """{"k":"local","name":${str(fname)}}""" }
	val capParams = capPairs.map { (decl, fname) -> """{"name":${str(fname)},"type":${str(captureFieldType(decl))}}""" }
	val ownParams = fn.parameters.filter { it.kind == IrParameterKind.Regular }.map { pj(it.name.asString(), it.type) }
	val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
	capPairs.forEach { (decl, _) -> captureSubst.remove(decl) }
	val ret = birType(fn.returnType)
	liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[${(capParams + ownParams).joinToString(",")}],"ret":${str(ret)},"body":[$body]}""")
}

/**
 * Lift a function-local class to a top-level synthetic type. Referenced outer locals (incl. the enclosing
 * `this`) become leading ctor params / capture fields; construction sites prepend those values (see the
 * IrConstructorCall handler). Returns a no-op statement (the declaration emits nothing inline).
 */
internal fun BirEmitter.liftLocalClass(klass: IrClass): String {
	if (anonNames.containsKey(klass)) return """{"k":"block","body":[]}"""   // already lifted
	val cname = "dotkt\$${klass.name.asString()}\$${scopeCounter++}"
	anonNames[klass] = cname
	val captured = capturedVarsForObject(klass)
	// Writing a captured outer local from the class needs heap ref-cells (same as the object-literal case).
	if (captured.any { it in mutatedIn(klass) && !isRefCell(it) })
		return unsupported(klass, "a local class that writes to a captured outer variable",
			"read-only capture works; pass the value in by constructor, or use a class field")
	// Capturing an enclosing type parameter isn't supported for a local class yet (it would need a generic lift +
	// constructed type uses) — a clear error beats invalid IL. A capturing lambda or local fun does support it.
	if (freeTypeParams(captured.map { it.type }).isNotEmpty())
		return unsupported(klass, "a local class that captures an enclosing generic type parameter",
			"move the logic into a (capturing) lambda or a local fun, which do support it")
	val capPairs = captured.map { it to captureFieldName(it) }
	capPairs.forEach { (decl, fname) ->
		captureSubst[decl] = """{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":${str(fname)}}"""
	}
	liftedTypes.add(typeDef(klass, capPairs, generated = true))
	capPairs.forEach { (decl, _) -> captureSubst.remove(decl) }
	localClassCaptures[klass] = captured
	return """{"k":"block","body":[]}"""
}
