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
import org.jetbrains.kotlin.ir.declarations.IrField
import org.jetbrains.kotlin.ir.declarations.IrProperty
import org.jetbrains.kotlin.ir.declarations.IrSimpleFunction
import org.jetbrains.kotlin.ir.declarations.IrVariable
import org.jetbrains.kotlin.ir.expressions.IrBlock
import org.jetbrains.kotlin.ir.expressions.IrBlockBody
import org.jetbrains.kotlin.ir.expressions.IrCall
import org.jetbrains.kotlin.ir.expressions.IrConst
import org.jetbrains.kotlin.ir.expressions.IrConstructorCall
import org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression
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
		(t.classifierOrNull as? IrTypeParameterSymbol)?.let {
			// A param's own BOUND may name a FURTHER param (`<T, U> where T : Comparable<U>`). The lift re-declares the
			// bound with the param, so it has to be generic over `U` as well or the re-declared constraint is unbound.
			if (acc.add(it.owner)) it.owner.superTypes.forEach(::walk)
		}
		if (t is IrSimpleType) t.arguments.forEach { (it as? IrTypeProjection)?.type?.let(::walk) }
	}
	types.forEach(::walk)
	return acc.toList()
}

/** Type operands USED in a function body (e.g. `x is R` / `x as R` / `R::class`). A lifted closure must be generic
 *  over these too: on the CLR generics are reified, so `is R` works once the lifted method carries `R` — that is the
 *  deliberate CLR form, where the JVM would need `reified`+inlining. freeTypeParams over (params+return+captures)
 *  alone misses a body-only `R`.
 *
 *  NOT included: the declared type of a body-LOCAL. Making the lift generic over it is not enough on its own — the
 *  lift's body still names every `tv` in the ENCLOSING frame and leans on ilemit's positional cross-scope fallback,
 *  so a local whose type is an enclosing variable needs the whole body re-expressed in the lift's own frame (the
 *  treatment bir2cir's ClosureSynthesis.RebindSyntheticTypeVariables gives a lifted closure class). Adding the type
 *  here alone converts bir2cir's loud refusal into invalid IL, so the refusal stands until that lands. */
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
 * Captures/params reuse the SAME machinery as the closure path (`capturedVars(includeThis=true)` /
 * `captureFieldType`). Each non-receiver capture gets a compiler-only `cap$` descriptor name, allocated by
 * DECLARATION IDENTITY rather than Kotlin source spelling. The body is emitted with each captured decl SHADOWED to
 * its bare DESCRIPTOR name
 * `{"k":"local","name":D}` (the (B) shadow below) — the ONE name the SM body uses — so bir2cir's SM builder rewrites
 * those captured-var reads into SM field reads by name; the construction VALUE rides a SEPARATE `capValues` channel in
 * the enclosing frame's vocabulary. typeArgs are the BARE enclosing type-param names (bir2cir prepends `gp:` when it
 * instantiates the open SM), NOT the `gp:`-prefixed form newClosure emits for ilemit.
 */
private fun BirEmitter.suspendLambda(node: IrFunctionExpression): String? {
	val fn = node.function
	// Own params in delegate order (contexts, extension receiver, then regulars) — matches lambdaParamsJson + bir2cir's
	// create() views (arity 0/1 = fixed create() slots, arity >= 2 = create(args, completion)). arity = the count.
	val ownParams = orderedLambdaParams(fn)
	// Restricted-suspension builders (`sequence { }`/`iterator { }`'s @RestrictsSuspension SequenceScope receiver)
	// now flow through this SAME suspend-lambda path: bir2cir gives the lambda the `RestrictedSuspendLambda` base
	// (not the plain SuspendLambda), so the cold-core builder runs. No exclusion here — kotc emits the pure suspend
	// facts and bir2cir picks the restricted base from the receiver scope's @RestrictsSuspension annotation.
	val captures = capturedVars(fn, includeThis = true)
	// The enclosing receiver keeps the established `__outer` descriptor because the SM body represents its reads as
	// `{k:this}`. Every ordinary capture is compiler-prefixed and collision-free: capture fields share a namespace with
	// the SM's generated `label` field and lambda-parameter fields, and Kotlin source names are not identities.
	val outerCapture = captures.firstOrNull { it.name.asString() == "<this>" }
	val capturePairsByIdentity = java.util.IdentityHashMap<IrValueDeclaration, String>()
	if (outerCapture != null) capturePairsByIdentity[outerCapture] = "__outer"
	uniqueCaptureNames(
		captures.filter { it !== outerCapture },
		mutableSetOf("__outer"),
		alwaysPrefix = true,
	).forEach { (d, name) -> capturePairsByIdentity[d] = name }
	val capturePairs = captures.map { it to capturePairsByIdentity.getValue(it) }
	val capturesJson = capturePairs.joinToString(",") { (d, name) ->
		"""{"name":${str(name)},"type":${str(captureFieldType(d))}}"""
	}
	// Per-slot capture VALUES (`capValues`) + the descriptor-name body shadow — "one frame, one name; one value
	// channel". For each captured decl the generated DESCRIPTOR name D is the ONLY name the SM body may use;
	// bir2cir's name-keyed spill rewrite (hot SuspendLambdaLowering + cold SuspendColdLowering) field-ifies D. The
	// construction VALUE is capValueExpr(d) in the ENCLOSING frame's vocabulary — computed HERE, BEFORE the (B) body
	// shadow — and is ALWAYS stamped into capValues[i]. bir2cir therefore never has to reconstruct a captured value
	// from the descriptor's generated name. Consumers: hot uses the value verbatim at construction; cold resolves it
	// through RewriteNoSpill into the constructing cold SM's field vocabulary.
	val capValueSlots = captures.map { d ->
		capValueExpr(d)   // enclosing-frame vocabulary — MUST precede the (B) body shadow below
	}
	val capValuesJson = if (capValueSlots.isEmpty()) "" else ""","capValues":[${capValueSlots.joinToString(",")}]"""
	val paramsJson = lambdaParamsJson(ownParams)
	val resultType = birType(fn.returnType)
	// Enclosing generic type params referenced by the SM (captures/params/return/body operands) -> open SM
	// instantiation. BARE names: bir2cir prepends `gp:`.
	val freeTps = freeTypeParams(captures.map { it.type } + fn.parameters.map { it.type } + listOf(fn.returnType) + bodyTypeOperands(fn))
		.sortedWith(compareBy<IrTypeParameter>(
			{ if (tvOf(it).scope == "type") 0 else 1 },
			{ tvOf(it).i },
		))
	// The SM's own generic-parameter NAME declarations (the enclosing free type params the state machine is
	// generic over) — a type-param DECLARATION list (bir2cir names the SM's params + instantiates `!i`), NOT a
	// type-USAGE slot, so it rides as the `typeParams` name shorthand (§2.5), consistent with the other lambda paths.
	val typeParamsBare = freeTps.joinToString(",") { str(it.name.asString()) }
	val typeArgsJson = if (freeTps.isEmpty()) "" else
		""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
	val extensionReceiver = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
	// (B) body shadow: emit the SM body with each captured decl bound to its DESCRIPTOR name `{k:local,name:D}` in
	// `captureSubst` by declaration identity, so the body names the capture EXACTLY as the descriptor declares it (the
	// name bir2cir's spill rewrite keys on). This deliberately does NOT touch name-keyed `valSubst`: a same-spelled
	// declaration is not the captured declaration. Saved + restored around the emission, mirroring samConversion.
	// `<this>` is the one descriptor with an established body spelling: ordinary `{k:this}`. In the lambda's OWN
	// frame bir2cir rewrites that spelling to its `__outer` capture field. Force that spelling after capValues was
	// computed so an enclosing carrier/closure substitution remains solely a CONSTRUCTION value and cannot leak a
	// caller-frame token/local into the lambda body.
	val shadowCap = java.util.IdentityHashMap<IrValueDeclaration, String?>()
	for ((d, name) in capturePairs) {
		shadowCap[d] = captureSubst[d]
		captureSubst[d] = if (d.name.asString() == "<this>")
			"""{"k":"this"}"""
		else
			"""{"k":"local","name":${str(name)}}"""
	}
	// A suspend extension lambda has two distinct `this` candidates: its own extension receiver and a captured
	// enclosing dispatch receiver. Preserve that distinction in BIR instead of asking bir2cir to infer it from a bare
	// `this`: the extension receiver is the leading physical lambda param, so bind its IrGetValue to that param by
	// identity while emitting the body. A remaining bare `this` can then only denote the captured enclosing instance.
	val hadExtensionReceiver = extensionReceiver != null && selfSubst.containsKey(extensionReceiver)
	val savedExtensionReceiver = extensionReceiver?.let { selfSubst[it] }
	if (extensionReceiver != null)
		selfSubst[extensionReceiver] = """{"k":"local","name":${str(extensionReceiver.name.asString())}}"""
	val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
	if (extensionReceiver != null) {
		if (hadExtensionReceiver) selfSubst[extensionReceiver] = savedExtensionReceiver!!
		else selfSubst.remove(extensionReceiver)
	}
	shadowCap.forEach { (d, prev) -> if (prev != null) captureSubst[d] = prev else captureSubst.remove(d) }
	return """{"k":"newSuspendLambda","arity":${ownParams.size},"captures":[$capturesJson]$capValuesJson,"params":[$paramsJson],"suspendRet":${str(resultType)},"typeParams":[$typeParamsBare]$typeArgsJson,"body":[$body],"funcType":${funcTypeOf(fn).toJson()}}"""
}

/** SHADOW the lambda's own regular params in `valSubst` while emitting its body: an enclosing lambda carrier
 *  may have bound the SAME name (e.g. `it`) to an outer local. A lifted lambda's params are its OWN method
 *  params — `IrGetValue` resolves them by NAME through `valSubst` (BirEmitterExpressions), so a stale outer
 *  binding would make the body reference a foreign local that is not in the lifted method's scope (`load unknown
 *  var` at ilemit). Removing them yields the correct bare `{"k":"local","name":<param>}`. Saved + restored,
 *  mirroring `emitInlineLambdaCarrier`. */
private inline fun <T> BirEmitter.withLambdaParamShadow(fn: IrSimpleFunction, block: () -> T): T {
	// [isValueParameter], matching what `lambdaParamsJson` DECLARES for this lift — shadowing only the regular ones
	// would leave a context parameter's body read bound to an enclosing carrier's same-named local.
	val names = fn.parameters.filter { isValueParameter(it) }.map { it.name.asString() }
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
			val recvName = lambdaRecvName(fn)
			val body = withLambdaSelf(fn, recvName) {
				withLambdaParamShadow(fn) { (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) } }
			}
			liftedMethods.add("""{"name":${str(lname)},"generated":true,"static":true,"override":false,"virtual":false$typeParams,"params":[${lambdaParamsJson(fn.parameters, recvName)}],"ret":${str(ret)},"body":[$body]}""")
		}
		val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
		return """{"k":"newDelegate","method":${str(lname)},"funcType":${str(ftype)}$typeArgs${localCalleeOwnerTag()}}"""
	}
	// Capturing: build a closure class. Captures rewrite to `this.<field>` (by symbol identity, so the
	// enclosing `this` — captured when the lambda reads a member — maps to a `__outer` field, not the
	// closure's own `this`). For a CPS suspend lambda the closure `invoke` is an INSTANCE coroutine; ilemit
	// captures the closure `this` into the state machine so resume can still read the captured-var fields.
	val cname = "dotkt\$${synthScope}\$Closure${closureCounter++}"
	val capPairs = uniqueCaptureNames(captures)
	// Save any prior substitution for each captured decl so the OUTER binding (e.g. an intrinsic block's `c`
	// bound to the coroutine's own continuation) is restored after the body — not blown away — so the capture
	// VALUE (capValueExpr below) is still evaluated correctly in the enclosing context.
	val savedSubst = capPairs.associate { (decl, _) -> decl to captureSubst[decl] }
	capPairs.forEach { (decl, fname) ->
		captureSubst[decl] = """{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":${str(fname)}}"""
	}
	val recvName = lambdaRecvName(fn)
	val body = withLambdaSelf(fn, recvName) {
		withLambdaParamShadow(fn) { (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) } }
	}
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
	val synthClass = """{"name":${str(cname)},"fields":[$fields],"params":[${lambdaParamsJson(fn.parameters, recvName)}],"ret":${str(ret)},"body":[$body]${typeParamsJson(freeTps)}}"""
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
	// A `suspend fun interface` (e.g. FlowCollector { emit(...) }) — the SAM method carries the pure suspend FACT via
	// `mods.suspend`, the discriminator bir2cir's SuspendColdLowering.IsMemberShapeEligible gates on. Without it a
	// suspend SAM `emit` body keeps its raw `suspendCall`-tagged nodes (never cold-transformed) — the whole flow
	// operator surface (the FlowCollector{} SAM emits: Combine/Merge/Transform/…) would dangle in ilemit. kotc reads
	// only `sam.isSuspend` (a pure Kotlin fact); the coroutine ABI lowering is entirely downstream.
	// `suspendRet` rides ALONGSIDE `mods.suspend` (the pairing `resultTypeJson` states for every other declaration
	// emitter): the modifier is the FACT, the slot is the Kotlin RESULT TYPE, and bir2cir's cold registry reads the
	// slot — a declaration carrying the modifier without it has had its result type dropped, which cost the suspend
	// SAM's awaited values their type. Same value as `ret` here, since `ret` is the lambda's own Kotlin return type.
	val samMods = if (sam.isSuspend) ""","mods":{"suspend":true},"suspendRet":${str(ret)}""" else ""
	val samMethod = """{"name":${str(samName)},"static":false,"override":true,"virtual":true,"params":[$samParams],"ret":${str(ret)}$samMods,"body":[$body]}"""
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
 * ilverify-clean), and a .NET-method deferral case. Suspend references do not take any of these delegate paths:
 * [suspendFunctionRef] uniformly adapts top-level/member/extension references to `newSuspendLambda`.
 */
internal fun BirEmitter.functionRef(node: IrFunctionReference): String {
	// `::Ctor` (constructor reference) -> a lifted static factory `__ctorref_N(args) = new T(args)`, bound as a
	// delegate (delegates can't bind a ctor directly). `Func<…,UserType>` now resolves via DelegateCtor.
	(node.symbol.owner as? IrConstructor)?.let { ctor ->
		val klass = ctor.parent as? IrClass
		if (klass != null && !isExternalNetType(klass)) {
			val ps = ctor.parameters.filter { it.kind == IrParameterKind.Regular }
			// A lifted local class constructor has hidden leading capture arguments. A delegate cannot target that
			// constructor (or a same-arity static factory) directly: bind the captures into an ordinary closure whose
			// invoke parameters remain exactly the Kotlin constructor-reference signature.
			localClassCaptures[klass]?.takeIf { it.isNotEmpty() }?.let { captures ->
				val cname = "dotkt\$${synthScope}\$Closure${closureCounter++}"
				val capPairs = uniqueCaptureNames(captures)
				val fields = capPairs.joinToString(",") { (decl, fname) ->
					"""{"name":${str(fname)},"type":${str(captureFieldType(decl))}}"""
				}
				val ctorArgs = (capPairs.map { (_, fname) ->
					"""{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":${str(fname)}}"""
				} + ps.map { """{"k":"local","name":${str(it.name.asString())}}""" }).joinToString(",")
				val ctorArgTypes = (capPairs.map { (decl, _) -> str(captureFieldType(decl)) } +
					ps.map { birType(it.type).toJson() }).joinToString(",")
				val retT = birType(ctor.returnType)
				val newE = """{"k":"new","type":${ownerSpec(klass, ctor.returnType).toJson()},"argTypes":[$ctorArgTypes],"args":[$ctorArgs]}"""
				val freeTps = freeTypeParams(
					captures.map { it.type } + ps.map { it.type } + listOf(ctor.returnType))
				val synthClass = """{"name":${str(cname)},"fields":[$fields],"params":[${lambdaParamsJson(ps)}],"ret":${str(retT)},"body":[{"k":"return","value":$newE}]${typeParamsJson(freeTps)}}"""
				val capExprs = captures.joinToString(",") { capValueExpr(it) }
				val typeArgs = if (freeTps.isEmpty()) "" else
					""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
				return """{"k":"newClosure","closureType":${fqnJson(cname)},"captures":[$capExprs],"method":"invoke","funcType":${TypeNode.Fn(false, retT, ps.map { birTypeDeleg(it.type) }).toJson()}$typeArgs,"synthClass":$synthClass}"""
			}
			val lname = "__ctorref${lambdaCounter++}"
			val psJson = ps.joinToString(",") { """{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}}""" }
			val argsJson = ps.joinToString(",") { """{"k":"local","name":${str(it.name.asString())}}""" }
			val argTypesJson = ps.joinToString(",") { birType(it.type).toJson() }
			val retT = birType(ctor.returnType)
			val newE = """{"k":"new","type":${ownerSpec(klass, ctor.returnType).toJson()},"argTypes":[$argTypesJson],"args":[$argsJson]}"""
			val freeTps = freeTypeParams(ps.map { it.type } + listOf(ctor.returnType))
			val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
			liftedMethods.add("""{"name":${str(lname)},"generated":true,"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${str(retT)},"body":[{"k":"return","value":$newE}]}""")
			return """{"k":"newDelegate","method":${str(lname)},"funcType":${TypeNode.Fn(false, retT, ps.map { birTypeDeleg(it.type) }).toJson()}$typeArgs${localCalleeOwnerTag()}}"""
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
			liftedMethods.add("""{"name":${str(lname)},"generated":true,"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${str(retT)},"body":[{"k":"return","value":$newE}]}""")
			return """{"k":"newDelegate","method":${str(lname)},"funcType":${TypeNode.Fn(false, retT, ps.map { birTypeDeleg(it.type) }).toJson()}$typeArgs${localCalleeOwnerTag()}}"""
		}
		return unsupported(node, "this constructor reference", "the constructor's class could not be resolved")
	}
	val fn = node.symbol.owner as? IrSimpleFunction
		?: return unsupported(node, "this function reference", "only references to plain (simple) functions are supported")
	// An ADAPTER reference (`obj::member` / `::member` whose signature needs coercing to the expected function type —
	// e.g. `MutableCollection<E>.add` (returns Boolean) referenced where a `(E) -> Unit` is expected): the frontend
	// synthesizes an adapter fn with origin ADAPTER_FOR_CALLABLE_REFERENCE, a REAL body that calls the reflection target
	// with the correct receiver + return coercion, and presents the bound instance as an EXTENSION-receiver param. The
	// naive `hasExt` branches below treat that as a TOP-LEVEL extension (`callStatic owner:null method:add`), which has
	// no such static — the `static method not found: add` ilemit fault (#84 G). Emit the adapter's own body instead
	// (like a lambda), so its faithful member `callInstance` + coercion survive.
	if (fn.origin == org.jetbrains.kotlin.ir.declarations.IrDeclarationOrigin.ADAPTER_FOR_CALLABLE_REFERENCE && fn.body != null)
		return adapterRef(node, fn)
	val referenceTypeArgs = functionReferenceTypeArgs(node, fn)
		?: return unsupported(node, "this generic function reference",
			"its FIR-resolved call-site type arguments are incomplete")
	val resolvedFuncType = birType(node.type) as? TypeNode.Fn
		?: return unsupported(node, "this function reference",
			"its FIR-resolved use-site type was not a function type")
	val dispatchIdx = fn.parameters.indexOfFirst { it.kind == IrParameterKind.DispatchReceiver }
	val hasExt = fn.parameters.any { it.kind == IrParameterKind.ExtensionReceiver }
	val ownerClass = fn.parent as? IrClass
	// A suspend function reference (`::suspendFn`, typed KSuspendFunctionN) -> a `newSuspendLambda` ADAPTER: the
	// suspend lambda `{ args -> target(args) }` whose body is a suspendCall to the target. A plain `newDelegate`
	// cannot carry the cold-suspend protocol (bir2cir has no suspend-delegate lowering; it DOES build a SuspendLambda
	// SM from `newSuspendLambda`). kotc emits ONLY the pure suspend FACTS (the `sfunc:`/suspend-`fn` funcType +
	// `suspendCall:true` on the inner call); bir2cir owns the SM transform. Covers top-level, member, and extension
	// references, whether their declaration is local or restored from a referenced DotKt assembly.
	if (fn.isSuspend) return suspendFunctionRef(node, fn, dispatchIdx)
	// `::topLevelFun` — no receiver: a delegate over the static file-class method. Carries `calleeOwner` (#199 Design
	// B, the SAME two-axis contract as a top-level FUNCTION call in BirEmitterCalls): `method` + the resolved parameter
	// `sig` select the overload, while the FIR-resolved callee file-class is the mandatory DISPATCH identity. Two
	// same-simple-name top-level funcs in DIFFERENT
	// packages (a.foo/b.foo) both emit `method:foo`; calleeOwner disambiguates to THIS package's foo. A dll2klib-
	// projected cross-module callee carries its file class too; any unresolved external shape fails at the
	// CIR invariant rather than falling back to a global first match.
	// The substitution axis is unchanged: a delegate over a top-level fun stays owner-less (no `owner` field here).
	// `::localFun` — a reference to a LOCAL fun, which is not a file-class member under its own name: it was lifted to
	// a static `__localN_<name>` whose captures are leading params.
	localFns[fn]?.let { (lname, caps, tps) ->
		val typeArgs = if (tps.isEmpty()) "" else ""","typeArgs":[${tps.joinToString(",") { tvOf(it).toJson() }}]"""
		// No captures: the delegate targets the lifted static directly.
		if (caps.isEmpty())
			return """{"k":"newDelegate","method":${str(lname)},"funcType":${funcTypeOf(fn).toJson()}$typeArgs${localCalleeOwnerTag()}}"""
		// WITH captures the delegate's arity cannot match the lifted static's (captures ride ahead of the declared
		// params), so the reference is a CLOSURE over those values whose `invoke` forwards to the lift — the same
		// `{ args -> bump(args) }` the user could write by hand, built from the same `synthClass` ingredients as the
		// lambda path. A generic lift is represented by re-declaring the same free Kotlin type parameters on this
		// closure fact; ClosureSynthesis rebinds the payload (including the forwarding call's typeArgs) into the
		// synthesized class's type-parameter scope.
		val cname = "dotkt\$${synthScope}\$Closure${closureCounter++}"
		val capPairs = uniqueCaptureNames(caps)
		val fields = capPairs.joinToString(",") { (decl, fname) ->
			"""{"name":${str(fname)},"type":${str(captureFieldType(decl))}}"""
		}
		val ownVps = fn.parameters.filter { it.kind == IrParameterKind.Regular }
		val callArgs = (capPairs.map { (_, fname) ->
			"""{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":${str(fname)}}"""
		} + ownVps.map { """{"k":"local","name":${str(it.name.asString())}}""" }).joinToString(",")
		val callSig = (capPairs.map { (decl, _) -> captureFieldType(decl).toJson() } +
			ownVps.map { birType(it.type).toJson() }).joinToString(",")
		val callE = """{"k":"callStatic","owner":null,"method":${str(lname)},"sig":[$callSig],"args":[$callArgs]$typeArgs${localCalleeOwnerTag()}}"""
		val retT = birType(fn.returnType)
		val invokeBody = if (fn.returnType.isUnit()) """{"k":"exprStmt","expr":$callE}"""
			else """{"k":"return","value":$callE}"""
		val synthClass = """{"name":${str(cname)},"fields":[$fields],"params":[${lambdaParamsJson(ownVps)}],"ret":${str(retT)},"body":[$invokeBody]${typeParamsJson(tps)}}"""
		val capExprs = caps.joinToString(",") { capValueExpr(it) }
		return """{"k":"newClosure","closureType":${fqnJson(cname)},"captures":[$capExprs],"method":"invoke","funcType":${funcTypeOf(fn).toJson()}$typeArgs,"synthClass":$synthClass}"""
	}
	// A direct static declaration loaded from a CLR reference KLIB has no dispatch receiver and is therefore not a
	// top-level file-facade function. Materialize the same neutral local adapter used for the older object-qualified
	// CLR static shape: the adapter body carries the external owner identity and static declaration fact, while
	// bir2cir remains responsible for resolving the concrete CLR member shape. This also gives the delegate a real
	// local calleeOwner instead of losing the declaring type on a bare newDelegate.
	if (dispatchIdx < 0 && !hasExt && ownerClass != null && isExternalNetType(ownerClass)) {
		val clrOwner = clrName(ownerClass)
			?: return unsupported(node, "this CLR static function reference", "its external owner identity is missing")
		val regs = fn.parameters.filter { it.kind == IrParameterKind.Regular }
		if (regs.size != resolvedFuncType.params.size)
			return unsupported(node, "this CLR static function reference",
				"its use-site function arity does not match the resolved declaration")
		val lname = "__mref${lambdaCounter++}"
		val psJson = regs.zip(resolvedFuncType.params).joinToString(",") { (p, t) ->
			"""{"name":${str(p.name.asString())},"type":${t.toJson()}}"""
		}
		val argsJson = regs.joinToString(",") { """{"k":"local","name":${str(it.name.asString())}}""" }
		val argTypes = regs.joinToString(",") { birType(it.type).toJson() }
		val anySlotTag = if (isAnySlotMethod(fn)) ""","anySlot":true""" else ""
		val callE = """{"k":"callStatic","ownerType":${fqnJson(clrOwner)},"method":${str(fn.name.asString())}${overloadSigField(fn)}$referenceTypeArgs,"argTypes":[$argTypes],"ret":${resolvedFuncType.ret.toJson()},"args":[$argsJson]$anySlotTag}"""
		val body = if (fn.returnType.isUnit()) """{"k":"exprStmt","expr":$callE}"""
			else """{"k":"return","value":$callE}"""
		val freeTps = freeTypeParams(listOf(node.type))
		val adapterTypeArgs = if (freeTps.isEmpty()) "" else
			""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
		liftedMethods.add("""{"name":${str(lname)},"generated":true,"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${resolvedFuncType.ret.toJson()},"body":[$body]}""")
		return """{"k":"newDelegate","method":${str(lname)},"funcType":${resolvedFuncType.toJson()}$adapterTypeArgs${localCalleeOwnerTag()}}"""
	}
	if (dispatchIdx < 0 && !hasExt) {
		// A generic top-level callable reference has already been instantiated by FIR at this use site. Preserve those
		// type arguments just like an ordinary call and an extension reference do; the declared `sig` alone selects the
		// generic definition but cannot close it for ldftn (`::handle<T>` otherwise reaches ilemit as an open method).
		val refTps = fn.typeParameters
		val refTaArgs = refTps.indices.map { node.typeArguments.getOrNull(it) }
		val refTa = if (refTps.isEmpty() || refTaArgs.any { it == null }) "" else
			""","typeArgs":[${refTaArgs.joinToString(",") { birType(it!!).toJson() }}]"""
		val refFuncType = birType(node.type) as? TypeNode.Fn
			?: return unsupported(node, "this top-level function reference",
				"its FIR-resolved use-site type was not a function type")
		if (refTps.isNotEmpty() && refTa.isNotEmpty()) {
			// A MethodSpec is a valid call operand, but a generic method definition is not a closed ldftn target on
			// every CLR. Materialize the ordinary Kotlin adapter the reference denotes: a non-generic delegate target
			// (or one generic only over its enclosing free parameters) whose body makes the explicit generic call.
			val valueParams = regularParams(fn)
			if (valueParams.size != refFuncType.params.size)
				return unsupported(node, "this generic top-level function reference",
					"its use-site function arity does not match the resolved declaration")
			val lname = "__fnref${lambdaCounter++}"
			val paramsJson = valueParams.zip(refFuncType.params).joinToString(",") { (p, t) ->
				"""{"name":${str(p.name.asString())},"type":${t.toJson()}}"""
			}
			val argsJson = valueParams.joinToString(",") { """{"k":"local","name":${str(it.name.asString())}}""" }
			val call = """{"k":"callStatic","owner":null,"method":${str(fn.name.asString())}${overloadSigField(fn)}$refTa,"args":[$argsJson],"ret":${refFuncType.ret.toJson()}${calleeOwnerTag(fn)}}"""
			val body = if (fn.returnType.isUnit()) """{"k":"exprStmt","expr":$call}"""
				else """{"k":"return","value":$call}"""
			val freeTps = freeTypeParams(listOf(node.type))
			val adapterTypeArgs = if (freeTps.isEmpty()) "" else
				""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
			liftedMethods.add("""{"name":${str(lname)},"generated":true,"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$paramsJson],"ret":${refFuncType.ret.toJson()},"body":[$body]}""")
			return """{"k":"newDelegate","method":${str(lname)},"funcType":${refFuncType.toJson()}$adapterTypeArgs${localCalleeOwnerTag()}}"""
		}
		return """{"k":"newDelegate","method":${str(fn.name.asString())}${overloadSigField(fn)},"funcType":${refFuncType.toJson()}$refTa${calleeOwnerTag(fn)}}"""
	}
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
		val retT = fnType.ret
		val retVoid = retT == TypeNode.Fqn("kotlin.Unit")   // the SUBSTITUTED return (fn's own T may resolve to Unit)
		val callE = extensionReferenceCall(node, fn, retT, callArgs)
		val body = if (retVoid) """{"k":"exprStmt","expr":$callE}""" else """{"k":"return","value":$callE}"""
		// freeTypeParams over node.type's SUBSTITUTED args (not the declared fn params — same call-site-type trap):
		// picks up only genuine ENCLOSING-context type vars (a `fun <E> …` scope), never the ext fn's OWN T (already
		// substituted away in node.type). The lifted static must be generic over those enclosing vars.
		val nodeTypeArgs = (node.type as? IrSimpleType)?.arguments?.mapNotNull { (it as? IrTypeProjection)?.type }.orEmpty()
		val freeTps = freeTypeParams(nodeTypeArgs)
		val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
		liftedMethods.add("""{"name":${str(lname)},"generated":true,"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${retT.toJson()},"body":[$body]}""")
		return """{"k":"newDelegate","method":${str(lname)},"funcType":${fnType.toJson()}$typeArgs${localCalleeOwnerTag()}}"""
	}
	// `obj::method` — a bound instance reference: a delegate whose target is the bound receiver. Only USER
	// classes (the method resolves via FindMethod); .NET-method / extension / unbound refs are deferred. `ownerType` is
	// already the exact declaring-class identity (the member analogue of calleeOwner); `sig` disambiguates overloads
	// within it and survives bir2cir's name-only declaration/slot rewrites unchanged.
	val boundRecv = if (dispatchIdx >= 0 && !hasExt) node.arguments.getOrNull(dispatchIdx) else null
	if (boundRecv != null && ownerClass != null && !isExternalNetType(ownerClass)) {
		val virtual = fn.modality != Modality.FINAL || fn.overriddenSymbols.isNotEmpty()
		return """{"k":"newBoundDelegate","ownerType":${fqnJson(typeName(ownerClass))},"method":${str(fn.name.asString())}${overloadSigField(fn)}$referenceTypeArgs,"virtual":$virtual,"recv":${expr(boundRecv)},"funcType":${resolvedFuncType.toJson()}${if (isAnySlotMethod(fn)) ""","anySlot":true""" else ""},"calleeOwner":${fqnJson(typeName(ownerClass))}}"""
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
		val callE = """{"k":"callInstance","ownerType":${fqnJson(typeName(ownerClass))},"virtual":$virtual,"recv":{"k":"local","name":"__self"},"method":${str(fn.name.asString())}${overloadSigField(fn)}$referenceTypeArgs,"args":[$argsJson]${if (isAnySlotMethod(fn)) ""","anySlot":true""" else ""}}"""
		val retVoid = fn.returnType.isUnit()
		val retT = birType(fn.returnType)
		val body = if (retVoid) """{"k":"exprStmt","expr":$callE}""" else """{"k":"return","value":$callE}"""
		val freeTps = freeTypeParams(listOf(fn.parameters[dispatchIdx].type) + ps.map { it.type } + listOf(fn.returnType))
		val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
		liftedMethods.add("""{"name":${str(lname)},"generated":true,"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${str(retT)},"body":[$body]}""")
		return """{"k":"newDelegate","method":${str(lname)},"funcType":${TypeNode.Fn(false, retT, listOf(selfT) + ps.map { birTypeDeleg(it.type) }).toJson()}$typeArgs${localCalleeOwnerTag()}}"""
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
		// A reference to a STATIC .NET method (`Util::triple`, a .NET static class surfaced as an object): the bound
		// receiver is an IrGetObjectValue — the SAME frontend fact the direct-call site reads as `isStatic`
		// (`recv is IrGetObjectValue`), NOT CLR knowledge. Lift a static forwarder `__mref(args) = Owner.member(args)`
		// (a callStatic, no `__self`) and make a plain newDelegate over it; bir2cir's NetInteropBinding reshapes the
		// inner callStatic to clrStatic. A genuine bound INSTANCE ref (a real object receiver) keeps the
		// newBoundDelegate below. Mirrors the unbound-instance forwarder just below, minus the receiver.
		if (boundRecv is IrGetObjectValue && !ownerClass.isCompanion) {
			val lname = "__mref${lambdaCounter++}"
			val psJson = regs.joinToString(",") { """{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}}""" }
			val argsJson = regs.joinToString(",") { """{"k":"local","name":${str(it.name.asString())}}""" }
			val retVoid = fn.returnType.isUnit()
			val retT = birType(fn.returnType)
			val callE = """{"k":"callStatic","ownerType":${fqnJson(clrOwner)},"method":${str(member)},"argTypes":[$argTypes],"ret":${birType(fn.returnType).toJson()},"args":[$argsJson]$anySlotTag}"""
			val body = if (retVoid) """{"k":"exprStmt","expr":$callE}""" else """{"k":"return","value":$callE}"""
			val freeTps = freeTypeParams(regs.map { it.type } + listOf(fn.returnType))
			val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
			liftedMethods.add("""{"name":${str(lname)},"generated":true,"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${str(retT)},"body":[$body]}""")
			return """{"k":"newDelegate","method":${str(lname)},"funcType":${TypeNode.Fn(false, retT, regs.map { birTypeDeleg(it.type) }).toJson()}$typeArgs${localCalleeOwnerTag()}}"""
		}
		if (boundRecv != null) {
			val companionCallTag = if (ownerClass.isCompanion) ""","companionCall":true""" else ""
			return """{"k":"newBoundDelegate","ownerType":${fqnJson(clrOwner)},"method":${str(member)}$referenceTypeArgs,"argTypes":[$argTypes],"virtual":$virtual,"recv":${expr(boundRecv)},"funcType":${resolvedFuncType.toJson()}$anySlotTag,"calleeOwner":${fqnJson(clrOwner)}$companionCallTag}"""
		}
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
			val callE = """{"k":"callInstance","ownerType":${fqnJson(clrOwner)},"method":${str(member)}$referenceTypeArgs,"argTypes":[$argTypes],"ret":${birType(fn.returnType).toJson()},"recv":{"k":"local","name":"__self"},"args":[$argsJson]$anySlotTag}"""
			val body = if (retVoid) """{"k":"exprStmt","expr":$callE}""" else """{"k":"return","value":$callE}"""
			val freeTps = freeTypeParams(listOf(fn.parameters[dispatchIdx].type) + regs.map { it.type } + listOf(fn.returnType))
			val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
			liftedMethods.add("""{"name":${str(lname)},"generated":true,"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${str(retT)},"body":[$body]}""")
			return """{"k":"newDelegate","method":${str(lname)},"funcType":${TypeNode.Fn(false, retT, listOf(selfT) + regs.map { birTypeDeleg(it.type) }).toJson()}$typeArgs${localCalleeOwnerTag()}}"""
		}
	}
	return unsupported(node, "a method reference to a .NET method (`::${fn.name}`)",
		"wrap the call in a lambda instead, e.g. `{ a -> x.${fn.name}(a) }`")
}

/** Method type arguments selected by FIR for a callable reference. An empty string means a non-generic target;
 * null means a generic definition was not closed and must never reach a delegate target or forwarding call. */
internal fun BirEmitter.functionReferenceTypeArgs(
	node: IrFunctionReference,
	fn: IrSimpleFunction,
): String? {
	if (fn.typeParameters.isEmpty()) return ""
	val args = fn.typeParameters.indices.map { node.typeArguments.getOrNull(it) }
	if (args.any { it == null }) return null
	return ""","typeArgs":[${args.joinToString(",") { birType(it!!).toJson() }}]"""
}

/**
 * A suspend function reference (`::suspendFn` / `obj::suspendMethod` / `Type::suspendMethod`, typed KSuspendFunctionN)
 * -> a `newSuspendLambda` ADAPTER: the suspend lambda `{ a1..an -> target(a1..an) }` whose body is a single
 * `suspendCall`-tagged call to the referenced function. kotc emits ONLY pure Kotlin facts (the suspend `fn` funcType +
 * `suspendCall:true`); bir2cir's SuspendLambdaLowering builds the SuspendLambda state machine from this node — a plain
 * suspend `newDelegate` has no cold-suspend lowering. The lambda's params come from the reference's RESOLVED type
 * (`birType(node.type)` as a suspend `Fn`, receiver-FIRST for an unbound reference). A BOUND receiver is captured into
 * a `__recv` field (evaluated once, eagerly, at reference-creation time) through the `capValues` channel
 * SuspendLambdaLowering consumes; an UNBOUND receiver is the lambda's leading param. This is one adapter for
 * top-level/member/extension references, including members restored from a referenced DotKt assembly.
 */
internal fun BirEmitter.suspendFunctionRef(node: IrFunctionReference, fn: IrSimpleFunction, dispatchIdx: Int): String {
	val fnType = birType(node.type) as? TypeNode.Fn
		?: return unsupported(node, "a suspend function reference",
			"its inferred type was not a resolvable suspend function type")
	val referenceTypeArgs = functionReferenceTypeArgs(node, fn)
		?: return unsupported(node, "a generic suspend function reference",
			"its FIR-resolved call-site type arguments are incomplete")
	val boundRecv = if (dispatchIdx >= 0) node.arguments.getOrNull(dispatchIdx) else null
	val extIdx = fn.parameters.indexOfFirst { it.kind == IrParameterKind.ExtensionReceiver }
	val boundExt = if (extIdx >= 0) node.arguments.getOrNull(extIdx) else null
	val ownerClass = fn.parent as? IrClass
	val companionClass =
		(boundRecv as? IrGetObjectValue)?.symbol?.owner?.takeIf { it.isCompanion }
			?: ownerClass?.takeIf { it.isCompanion }
	val semanticCompanionType = (boundRecv?.let { birType(it.type) } as? TypeNode.Fqn)
		?.takeIf { ".<companion:" in it.name }
	val member = fn.name.asString()
	val ret = fnType.ret
	val virtual = fn.modality != Modality.FINAL || fn.overriddenSymbols.isNotEmpty()
	val anySlotTag = if (isAnySlotMethod(fn)) ""","anySlot":true""" else ""
	// The lambda's OWN physical params = its extension receiver (when the callable type has one), then ordinary
	// params. TypeNode.Fn deliberately keeps a Kotlin extension receiver in `recv` rather than duplicating it in
	// `params`; newSuspendLambda's invoke/create protocol still receives it as physical arg0, exactly like source
	// receiver lambdas (`suspend T.() -> R`).
	val physicalParamTypes = listOfNotNull(fnType.recv) + fnType.params
	val paramNames = physicalParamTypes.indices.map { "__p$it" }
	val paramsJson = physicalParamTypes.mapIndexed { i, t ->
		"""{"name":${str(paramNames[i])},"type":${t.toJson()}}"""
	}.joinToString(",")
	fun localArg(name: String) = """{"k":"local","name":${str(name)}}"""
	// Build (captures, capValues, bodyCall) by reference kind. The inner call MIRRORS the direct-call shape for this
	// callee so bir2cir attributes it identically; `suspendCall:true` is the fact SuspendColdLowering's FunGen segments on.
	val captures: String; val capValues: String?; val bodyCall: String
	when {
		extIdx >= 0 && dispatchIdx >= 0 ->
			return unsupported(node, "a suspend member extension-function reference",
				"a member extension function cannot be referenced as a callable in Kotlin")
		extIdx >= 0 && boundExt != null -> {
			// Bound top-level extension `value::ext`: capture the extension receiver exactly once, just as the
			// dispatch receiver of `value::member`; the lambda's own params are the target's regular args.
			captures = """{"name":"__recv","type":${birType(boundExt.type).toJson()}}"""
			capValues = expr(boundExt)
			val args = (listOf(localArg("__recv")) + paramNames.map(::localArg)).joinToString(",")
			bodyCall = extensionReferenceCall(node, fn, ret, args, suspending = true)
		}
		extIdx >= 0 -> {
			// Unbound top-level extension `Type::ext`: the extension receiver is the leading lambda parameter.
			if (paramNames.isEmpty())
				return unsupported(node, "a suspend extension-function reference",
					"its inferred type carries no extension-receiver parameter")
			captures = ""; capValues = null
			val args = paramNames.map(::localArg).joinToString(",")
			bodyCall = extensionReferenceCall(node, fn, ret, args, suspending = true)
		}
		dispatchIdx >= 0 && boundRecv != null && ownerClass != null -> {
			// Bound member `obj::m` — receiver captured into `__recv`; all lambda params are the target's regular args.
			val args = paramNames.joinToString(",") { localArg(it) }
			captures = """{"name":"__recv","type":${birType(boundRecv.type).toJson()}}"""
			capValues = expr(boundRecv)
			val semanticOwner = companionClass ?: ownerClass
			val externalCompanionOwner = companionClass?.let { clrExternalOwner(it) }
			val owner = externalCompanionOwner?.let { fqnJson(it) }
				?: semanticCompanionType?.toJson()
				?: ownerSpec(semanticOwner, boundRecv.type).toJson()
			val companionCallTag = if (externalCompanionOwner != null) ""","companionCall":true""" else ""
			bodyCall = """{"k":"callInstance","ownerType":$owner,"virtual":$virtual,"recv":${localArg("__recv")},"method":${str(member)}${overloadSigField(fn)}$referenceTypeArgs,"args":[$args]$anySlotTag,"suspendCall":true$companionCallTag}"""
		}
		dispatchIdx >= 0 && boundRecv == null && ownerClass != null -> {
			// Unbound member `Type::m` — the leading lambda param is the receiver, the rest are the target's regular args.
			if (paramNames.isEmpty())
				return unsupported(node, "a suspend function reference",
					"its inferred type carries no dispatch-receiver parameter")
			val args = paramNames.drop(1).joinToString(",") { localArg(it) }
			captures = ""; capValues = null
			bodyCall = """{"k":"callInstance","ownerType":${ownerSpec(ownerClass, fn.parameters[dispatchIdx].type).toJson()},"virtual":$virtual,"recv":${localArg(paramNames.first())},"method":${str(member)}${overloadSigField(fn)}$referenceTypeArgs,"args":[$args]$anySlotTag,"suspendCall":true}"""
		}
		dispatchIdx < 0 -> {
			// Top-level `::fn` — all lambda params are the target's regular args; an owner:null static call.
			val args = paramNames.joinToString(",") { localArg(it) }
			captures = ""; capValues = null
			bodyCall = """{"k":"callStatic","owner":null,"method":${str(member)}${overloadSigField(fn)}$referenceTypeArgs,"args":[$args],"suspendCall":true${calleeOwnerTag(fn)}}"""
		}
		else -> return unsupported(node, "a suspend function reference",
			"its receiver could not be resolved to a supported (top-level or user-member) suspend target")
	}
	val body = if (ret == TypeNode.Fqn("kotlin.Unit")) """{"k":"exprStmt","expr":$bodyCall}""" else """{"k":"return","value":$bodyCall}"""
	val capValuesJson = if (capValues == null) "" else ""","capValues":[$capValues]"""
	// The SM must be generic over any enclosing type params in the reference's signature (reified CLR generics); bare
	// names (bir2cir prepends `gp:`), consistent with suspendLambda().
	val freeTps = freeTypeParams(listOf(node.type) + listOfNotNull(boundRecv?.type, boundExt?.type))
		.sortedWith(compareBy<IrTypeParameter>(
			{ if (tvOf(it).scope == "type") 0 else 1 },
			{ tvOf(it).i },
		))
	val typeParamsBare = freeTps.joinToString(",") { str(it.name.asString()) }
	val typeArgsJson = if (freeTps.isEmpty()) "" else
		""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
	val externalCompanionOwnerTag = companionClass?.let { clrExternalOwner(it) }
		?.let { ""","externalCompanionOwner":${str(it)}""" } ?: ""
	val localCompanionCaptureOwner = semanticCompanionType?.name
		?: companionClass?.takeIf { clrExternalOwner(it) == null }?.let { typeName(it) }
	val localCompanionCaptureTag = localCompanionCaptureOwner
		?.let { ""","companionCaptureOwner":${str(it)}""" } ?: ""
	return """{"k":"newSuspendLambda","arity":${physicalParamTypes.size},"captures":[$captures]$capValuesJson,"params":[$paramsJson],"suspendRet":${ret.toJson()},"typeParams":[$typeParamsBare]$typeArgsJson,"body":[$body],"funcType":${fnType.toJson()}$externalCompanionOwnerTag$localCompanionCaptureTag}"""
}

/**
 * Builds the forwarding call shared by bound/unbound, ordinary/suspend top-level extension references. The receiver
 * is already the first item in [callArgs]. A dll2klib declaration carries its referenced file-facade identity in
 * `@ClrExternal`; a source-graph declaration keeps `owner:null` plus `calleeOwner`. Generic references carry their
 * call-site-instantiated type arguments so bir2cir/ilemit can bind the physical method consistently.
 */
internal fun BirEmitter.extensionReferenceCall(
	node: IrFunctionReference,
	fn: IrSimpleFunction,
	ret: TypeNode,
	callArgs: String,
	suspending: Boolean = false,
): String {
	val refTps = fn.typeParameters
	val refTaArgs = refTps.indices.map { node.typeArguments.getOrNull(it) }
	val hasRefTa = refTps.isNotEmpty() && refTaArgs.all { it != null }
	val refTa = if (!hasRefTa) "" else
		""","typeArgs":[${refTaArgs.joinToString(",") { birType(it!!).toJson() }}]"""
	val suspendTag = if (suspending) ""","suspendCall":true""" else ""
	val externalFileClass = clrExternalOwner(fn)
	if (externalFileClass != null) {
		val extRecvParam = fn.parameters.first { it.kind == IrParameterKind.ExtensionReceiver }
		val declShapeTypes = (listOf(extRecvParam) + regularParams(fn))
			.joinToString(",") { birType(it.type).toJson() }
		return if (hasRefTa)
			"""{"k":"callStatic","ownerType":${fqnJson(externalFileClass)},"method":${str(fn.name.asString())}$refTa,"shapeTypes":[$declShapeTypes],"args":[$callArgs]$suspendTag}"""
		else
			"""{"k":"callStatic","ownerType":${fqnJson(externalFileClass)},"method":${str(fn.name.asString())},"argTypes":[$declShapeTypes],"ret":${ret.toJson()},"args":[$callArgs]$suspendTag}"""
	}
	return """{"k":"callStatic","owner":null,"method":${str(fn.name.asString())}${overloadSigField(fn)}$refTa${retHintStr(hasRefTa, ret)},"args":[$callArgs]$suspendTag${calleeOwnerTag(fn)}}"""
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
	val callE = extensionReferenceCall(node, fn, retT, callArgs)
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
 * An ADAPTER callable reference (`ADAPTER_FOR_CALLABLE_REFERENCE`): the frontend has already synthesized a fn with a
 * REAL body that forwards to the reflection target with the correct receiver + return coercion (`MutableCollection.add`
 * referenced as `(E) -> Unit` -> `{ receiver.add(p0) }`, the Boolean result discarded to Unit). We emit that body
 * VERBATIM as a lambda/closure — the SAME shape `lambda()` produces for an `IrFunctionExpression` — so the faithful
 * member `callInstance` and its coercion survive, instead of the naive `hasExt` branches misreading the adapter's
 * synthesized extension receiver as a top-level extension (`callStatic owner:null`, the #84 G `static method not found`).
 * The adapter presents its bound instance as a receiver param: a BOUND receiver (its `node.arguments[idx]` is present)
 * is captured into a field; an UNBOUND receiver becomes a leading `invoke` param. Regular params are the remaining
 * `invoke` params. This subsumes bound/unbound member/extension adapters uniformly (the frontend already resolved the
 * inner call shape in the body).
 */
internal fun BirEmitter.adapterRef(node: IrFunctionReference, fn: IrSimpleFunction): String {
	val recvParams = fn.parameters.withIndex()
		.filter { it.value.kind == IrParameterKind.DispatchReceiver || it.value.kind == IrParameterKind.ExtensionReceiver }
	val boundCaps = ArrayList<Pair<IrValueParameter, IrExpression>>()   // adapter receiver param -> its bound value expr
	val leadingParams = ArrayList<IrValueParameter>()                   // unbound receiver params -> leading invoke params
	for ((idx, p) in recvParams) {
		val boundArg = node.arguments.getOrNull(idx)
		if (boundArg != null) boundCaps.add(p to boundArg) else leadingParams.add(p)
	}
	val regParams = fn.parameters.filter { it.kind == IrParameterKind.Regular }
	val invokeDecls = leadingParams + regParams
	val ret = birType(fn.returnType)
	// The call-site-resolved delegate type (`node.type` = the adapted `(E) -> Unit`), NOT `funcTypeOf(fn)` which would
	// carry the adapter's own receiver param.
	val fnType = (birType(node.type) as? TypeNode.Fn) ?: funcTypeOf(fn)
	val invokeParamsJson = invokeDecls.joinToString(",") { p ->
		val ty = if (p.type.isUnit()) TypeNode.Fqn("kotlin.Unit") else birTypeDeleg(p.type)
		"""{"name":${str(p.name.asString())},"type":${ty.toJson()}}"""
	}
	// The tv tokens baked into the synthClass (field/param/body/constraint types) carry their ORIGINAL scope+index
	// (`tvOf`: a method param -> `tv method <param.index>`, an enclosing-class param -> `tv type <flattened idx>`).
	// ilemit's ResolveTv maps a tv to the closure's OWN type param by matching that index against the declared
	// POSITION, so freeTps MUST be declared in original-index order — else a capture whose type is a later-declared
	// param (`C` in `fun <E, C : MutableCollection<E>>`) lands at the wrong position and its constraint resolves against
	// the wrong sibling (the `C violates the constraint of type parameter C` TypeLoadException). Type-scope params
	// precede method-scope (matching the flattened enclosing-first convention).
	val freeTps = freeTypeParams(boundCaps.map { it.first.type } + invokeDecls.map { it.type } + listOf(fn.returnType) + bodyTypeOperands(fn))
		.sortedWith(compareBy({ if (tvOf(it).scope == "type") 0 else 1 }, { tvOf(it).i }))
	val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
	// No captured receiver (`Type::member` fully unbound) -> a lifted static delegate; the leading receiver param(s) ride
	// as the forwarder's own params (referenced by name in the body).
	if (boundCaps.isEmpty()) {
		val lname = "__mref${lambdaCounter++}"
		val body = withLambdaParamShadow(fn) { (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) } }
		liftedMethods.add("""{"name":${str(lname)},"generated":true,"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$invokeParamsJson],"ret":${str(ret)},"body":[$body]}""")
		return """{"k":"newDelegate","method":${str(lname)},"funcType":${str(fnType)}$typeArgs${localCalleeOwnerTag()}}"""
	}
	// Bound receiver(s) -> a capture-class closure (mirrors `lambda()`'s capturing branch); the bound value(s) are the
	// capture exprs, and the adapter body's receiver-param reads rewrite to the capture fields via `captureSubst`.
	val cname = "dotkt\$${synthScope}\$Closure${closureCounter++}"
	val capPairs = boundCaps.map { (p, _) -> p to captureFieldName(p) }
	val savedSubst = capPairs.associate { (decl, _) -> decl to captureSubst[decl] }
	capPairs.forEach { (decl, fname) ->
		captureSubst[decl] = """{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":${str(fname)}}"""
	}
	val body = withLambdaParamShadow(fn) { (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) } }
	capPairs.forEach { (decl, _) -> val prev = savedSubst[decl]; if (prev != null) captureSubst[decl] = prev else captureSubst.remove(decl) }
	val fields = capPairs.joinToString(",") { (decl, fname) -> """{"name":${str(fname)},"type":${captureFieldType(decl).toJson()}}""" }
	val capExprs = boundCaps.joinToString(",") { (_, valueExpr) -> expr(valueExpr) }
	val synthClass = """{"name":${str(cname)},"fields":[$fields],"params":[$invokeParamsJson],"ret":${str(ret)},"body":[$body]${typeParamsJson(freeTps)}}"""
	return """{"k":"newClosure","closureType":${fqnJson(cname)},"captures":[$capExprs],"method":"invoke","funcType":${str(fnType)}$typeArgs,"synthClass":$synthClass}"""
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
	"""{"k":"new","type":${TypeNode.Fqn("kotlin.reflect.ClrPropertyStub", listOf(OBJ)).toJson()},"argTypes":[${fqnJson("kotlin.String")}],"args":[{"k":"const","type":${fqnJson("kotlin.String")},"value":${str(name)}}]}"""

/**
 * A genuine callable reference to a property (`::x`, `obj::p`, `Type::p`) -> a lifted class implementing the
 * REAL emitted stdlib `kotlin.reflect.KProperty0`/`KMutableProperty0`/`KProperty1`/`KMutableProperty1<…>`
 * interface (mirrors `samConversion`: a KProperty interface has no faithful .NET delegate representation, so
 * this is a lifted CLASS via `new`, not a `newDelegate`). `node.type` already carries FIR's resolved interface
 * identity + its (possibly generic) V/T arguments — reused verbatim as both the `interfaces` entry and the
 * get/set param types, so a captured enclosing generic (`Box<T>::value`) resolves through the SAME `birType`/
 * `typeArgSubst` machinery any other reference does (no separate remap here).
 *
 * Supported scope: a TOP-LEVEL property (`::x`), a MEMBER property either BOUND (`obj::p`, receiver captured in a
 * field) or UNBOUND (`Type::p`, receiver becomes the `get`/`set`'s own leading param), and a TOP-LEVEL extension
 * property in its bound/unbound forms — including generic and referenced-assembly accessors. A MEMBER extension
 * property with both dispatch and extension receivers (`KProperty2`) and a `length` reference RESOLVED on a
 * .NET-mapped CharSequence owner (String/StringBuilder/the polymorphic kotlin.CharSequence — bir2cir renames its
 * slot; a USER CharSequence implementer is faithful) are clean deferrals. `lateinit var` and `@ClrField` member
 * references use the backing-field path below. The compiler-synthesized KProperty argument of a delegate's
 * getValue/setValue/provideDelegate is NOT this path — those call sites materialize `kPropertyStub` directly
 * without going through `expr()`/this dispatch; the origin check below is a defensive fallback only.
 */
internal fun BirEmitter.propertyRef(node: IrPropertyReference): String {
	if (node.origin == IrStatementOrigin.PROPERTY_REFERENCE_FOR_DELEGATE)
		return kPropertyStub(node.symbol.owner.name.asString())
	// A TOP-LEVEL extension property reference (`obj::extProp` bound, or `Type::extProp` unbound) IS supported: it
	// lowers below through a static `<name>` accessor call carrying the ext receiver as the leading arg, MIRRORING
	// the top-level ext-property value-read path in `call()` (#21). Only a genuine KProperty2 — a MEMBER extension
	// property with BOTH a dispatch AND an extension receiver, inexpressible as a plain callable in Kotlin — stays
	// unsupported. Test the accessor's PARAMETER SHAPE, not the bound ARGUMENT (a bound test is null for an UNBOUND
	// ref, so it would misclassify an unbound top-level ext ref).
	val extAccessor = node.getter?.owner ?: node.setter?.owner
	val hasExtRecv = extAccessor?.parameters?.any { it.kind == IrParameterKind.ExtensionReceiver } == true
	val hasDispatchRecv = extAccessor?.parameters?.any { it.kind == IrParameterKind.DispatchReceiver } == true
	if (hasExtRecv && hasDispatchRecv)
		return unsupported(node, "this property reference",
			"a member extension-receiver property reference (KProperty2) has no supported lowering yet")
	val prop = node.symbol.owner
	// A `lateinit var` / `@ClrField` member property has PLAIN-BACKING-FIELD storage (no get_/set_ accessor slot) —
	// its get/set below read/write the field directly (`lateinitGet`/`field`/`setFieldExpr`), the SAME nodes the
	// ordinary member-property access path emits for these (BirEmitterCalls). `fieldBacked` gates that in readBody/
	// setMethod; everything else (interface identity, receiver capture, generics) is shared with the accessor path.
	val fieldBacked = prop.isLateinit || isClrField(prop)
	val getterFn = node.getter?.owner ?: prop.getter
		?: return unsupported(node, "this property reference", "the referenced property has no getter")
	// #57: a `length` reference whose accessor is RESOLVED on a .NET-MAPPED CharSequence owner is a clean deferral —
	// its slot is renamed/collapsed by bir2cir (`get_length` -> System.String/StringBuilder `get_Length`, or the
	// polymorphic kotlin.CharSequence face), a bir2cir-owned rewrite the lift's plain get_/set_ accessor call cannot
	// express. The discriminator is the ACCESSOR's RESOLVED declaring owner (`getterFn.parent`), NOT an
	// override-chain walk: fir2ir materializes a per-class fake override, so a user `class B : A`, `A : CharSequence`
	// resolves `B::length`'s getter in B (owner = B) — a user class whose OWN emitted `get_length` slot (its
	// synthesized `dotkt$CharSequence` implementation) the lift names faithfully, DIRECT or INHERITED. A `String`/
	// `StringBuilder`/bare-`CharSequence` receiver resolves the getter in the .NET-mapped owner itself, where the
	// rename bites. (The retired override-chain walk conflated the two — every user override transitively reaches
	// kotlin.CharSequence — so it over-deferred the direct user-class case while missing the indirect one.)
	val declOwnerFq = (getterFn.parent as? IrClass)?.fqNameWhenAvailable?.asString()
	if (prop.name.asString() == "length"
		&& declOwnerFq in setOf("kotlin.CharSequence", "kotlin.String", "kotlin.text.StringBuilder"))
		return unsupported(node, "this property reference",
			"a length reference addressed at a .NET-mapped CharSequence owner has no supported lowering yet")
	val setterFn = if (prop.isVar) (node.setter?.owner ?: prop.setter) else null
	val declClass = getterFn.parent as? IrClass
	val name = prop.name.asString()
	val boundRecv = propRefDispatchReceiver(node)
	// The bound EXTENSION receiver of a top-level ext-property reference (`obj::extProp`) — null when UNBOUND
	// (`Type::extProp`). Indexed off the getter/setter's ExtensionReceiver parameter slot (mirrors
	// propRefDispatchReceiver, one receiver-kind over).
	val extBoundRecv: IrExpression? = if (hasExtRecv) run {
		val params = (node.getter?.owner ?: node.setter?.owner)?.parameters
		val idx = params?.indexOfFirst { it.kind == IrParameterKind.ExtensionReceiver } ?: -1
		if (idx in 0 until node.arguments.size) node.arguments[idx] else null
	} else null
	// dll2klib carries the physical file-class owner directly on the projected property.
	val externalExtPropFileClass = if (hasExtRecv) clrExternalOwner(prop) else null
	// The receiver captured into the lift's `__recv` field for a BOUND reference: the dispatch receiver (a member
	// property) or the extension receiver (a top-level ext property). Only one is ever present.
	val capturedRecv = boundRecv ?: extBoundRecv
	// This is a semantic companion receiver capture, not an arbitrary object capture. bir2cir consumes the marker
	// when it chooses the companion's physical representation.
	val companionCaptureClass =
		(boundRecv as? IrGetObjectValue)?.symbol?.owner?.takeIf { it.isCompanion }
			?: declClass?.takeIf { it.isCompanion }
	val semanticCompanionType = (capturedRecv?.let { birType(it.type) } as? TypeNode.Fqn)
		?.takeIf { ".<companion:" in it.name }
	val companionCaptureOwner = semanticCompanionType?.name
		?: companionCaptureClass?.let { clrExternalOwner(it) ?: typeName(it) }
	val companionCaptureTag = companionCaptureOwner?.let { ""","companionCaptureOwner":${str(it)}""" } ?: ""

	val ifaceSpec = birType(node.type) as? TypeNode.Fqn
		?: return unsupported(node, "this property reference",
			"its inferred type was not a KProperty/KMutableProperty interface")
	val ifaceArgs = ifaceSpec.args.orEmpty()
	val arity0 = ifaceSpec.name == "kotlin.reflect.KProperty0" || ifaceSpec.name == "kotlin.reflect.KMutableProperty0"
	val vType = ifaceArgs.lastOrNull() ?: OBJ
	val recvTypeNode = ifaceArgs.getOrNull(0).takeIf { !arity0 }   // KProperty1/KMutableProperty1's T (unbound only)
	// A direct static declaration from a CLR reference KLIB is a KProperty0/KMutableProperty0 despite its Kotlin 2.4
	// fake accessor declaring a synthetic dispatch parameter. Classify from the resolved callable-reference type and
	// absence of a captured receiver, not that wrapper parameter. A real companion member reference captures its
	// singleton and therefore stays on the ordinary bound path below.
	val externalStatic = arity0 && capturedRecv == null && declClass != null &&
		isExternalNetType(declClass) && !hasExtRecv
	val externalStaticOwner = if (externalStatic) clrName(declClass) else null
	if (externalStatic && externalStaticOwner == null)
		return unsupported(node, "this CLR static property reference", "its external owner identity is missing")

	// BOUND (KProperty0, receiver captured in `__recv`) vs UNBOUND (KProperty1, receiver = the get/set's leading
	// param), for BOTH a member property (`declClass != null`) and a top-level extension property (`hasExtRecv`).
	// A top-level NON-ext property (`::x`) is neither — it stays the plain static-field/get_ path below.
	val bound = arity0 && capturedRecv != null && (declClass != null || hasExtRecv)
	val unbound = !arity0 && (declClass != null || hasExtRecv)
	val cname = "dotkt\$${synthScope}\$PropRef${closureCounter++}"

	fun recvExprIn(): String = when {
		bound -> """{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":"__recv"}"""
		unbound -> """{"k":"local","name":"receiver"}"""
		else -> """{"k":"this"}"""
	}
	val memberOwner: TypeNode = when {
		externalStatic -> TypeNode.Fqn(externalStaticOwner!!)
		bound && semanticCompanionType != null -> semanticCompanionType
		bound && companionCaptureClass != null -> ownerSpec(companionCaptureClass, boundRecv!!.type)
		bound && declClass != null -> ownerSpec(declClass, boundRecv!!.type)
		unbound && declClass != null -> recvTypeNode ?: OBJ
		else -> OBJ
	}
	// A top-level EXTENSION property: its getter/setter is a static `<name>` accessor (a `prop:get`/`prop:set` marker
	// carrying the ext receiver as its leading arg), NOT an instance `get_/set_` slot. Mirror the direct value-read
	// path: local/stdlib accessors use `owner:null`; a restored DotKt accessor carries its metadata-derived file class.
	// In both cases kotc projects the resolved Kotlin accessor identity and bir2cir chooses the CLR accessor shape.
	fun extAccessorCall(isSetter: Boolean, valueArg: String?): String {
		val kind = if (isSetter) "set" else "get"
		val recvArg = if (bound)
			"""{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":"__recv"}"""
		else """{"k":"local","name":"receiver"}"""
		val args = listOfNotNull(recvArg, valueArg).joinToString(",")
		// The `sig` overload key MUST match the accessor being called: the setter's param list is [recv, value], the
		// getter's is [recv]. Using the getter's sig for `prop:set` would key a same-name `var`-ext-property overload
		// to the wrong slot.
		val sigFn = if (isSetter) setterFn!! else getterFn
		// A generic extension property reference carries the property accessor's OWN resolved type arguments on the
		// reference node (`List<Int>::lastIndex` -> T=Int). Thread them onto the forwarded accessor call exactly as the
		// direct value-read path does, so bir2cir/ilemit close the generic method rather than seeing an open `!!T`.
		val refTps = sigFn.typeParameters
		val refTaArgs = refTps.indices.map { node.typeArguments.getOrNull(it) }
		val hasRefTa = refTps.isNotEmpty() && refTaArgs.all { it != null }
		val refTa = if (!hasRefTa) "" else
			""","typeArgs":[${refTaArgs.joinToString(",") { birType(it!!).toJson() }}]"""
		val retT = if (isSetter) TypeNode.Fqn("kotlin.Unit") else vType
		if (externalExtPropFileClass != null) {
			val extRecvParam = sigFn.parameters.first { it.kind == IrParameterKind.ExtensionReceiver }
			val declShapeTypes = (listOf(extRecvParam) + regularParams(sigFn))
				.joinToString(",") { birType(it.type).toJson() }
			return if (hasRefTa)
				"""{"k":"callStatic","ownerType":${fqnJson(externalExtPropFileClass)},"method":${str(name)},"prop":${str(kind)}$refTa,"shapeTypes":[$declShapeTypes],"args":[$args]}"""
			else
				"""{"k":"callStatic","ownerType":${fqnJson(externalExtPropFileClass)},"method":${str(name)},"prop":${str(kind)},"argTypes":[$declShapeTypes],"ret":${retT.toJson()},"args":[$args]}"""
		}
		return """{"k":"callStatic","owner":null,"method":${str(name)},"prop":${str(kind)}${overloadSigField(sigFn)}$refTa${retHintStr(hasRefTa, retT)},"args":[$args]${calleeOwnerTag(sigFn)}}"""
	}
	fun accessorCall(isSetter: Boolean, extraArg: String?): String {
		val fn = if (isSetter) setterFn!! else getterFn
		val virtual = fn.modality != Modality.FINAL || fn.overriddenSymbols.isNotEmpty()
		val args = listOfNotNull(extraArg).joinToString(",")
		val method = str((if (isSetter) "set_" else "get_") + name)
		val externalCompanionTag = companionCaptureClass?.takeIf { clrExternalOwner(it) != null }
			?.let { ""","companionCall":true""" } ?: ""
		return """{"k":"callInstance","ownerType":${memberOwner.toJson()},"virtual":$virtual,"recv":${recvExprIn()},"method":$method${overloadSigField(fn)},"args":[$args]$externalCompanionTag}"""
	}
	// A field-backed member property (`lateinit var`/`@ClrField`) reads/writes its backing field directly — the SAME
	// `lateinitGet`/`field`/`setFieldExpr` shapes the ordinary member-access path emits — over the lift's receiver
	// (bound = the captured `__recv` field, unbound = the `receiver` param).
	fun fieldAccess(isSetter: Boolean, valueArg: String?): String = when {
		isSetter -> """{"k":"setFieldExpr","ownerType":${memberOwner.toJson()},"recv":${recvExprIn()},"name":${str(name)},"value":$valueArg}"""
		prop.isLateinit -> """{"k":"lateinitGet","ownerType":${memberOwner.toJson()},"recv":${recvExprIn()},"name":${str(name)}}"""
		// A field read carries the constructed-generic ret hint (a `tv`-typed field on `B<T>`) — parity with the
		// ordinary member field-read path (BirEmitterCalls).
		else -> """{"k":"field","ownerType":${memberOwner.toJson()},"recv":${recvExprIn()},"name":${str(name)}${retHint((memberOwner as? TypeNode.Fqn)?.args != null, getterFn.returnType)}}"""
	}
	fun externalStaticAccess(isSetter: Boolean, valueArg: String?): String {
		val owner = memberOwner.toJson()
		if (fieldBacked) return if (isSetter)
			"""{"k":"staticFieldSet","ownerType":$owner,"name":${str(name)},"value":$valueArg}"""
		else """{"k":"staticField","ownerType":$owner,"name":${str(name)},"ret":${vType.toJson()}}"""
		val kind = if (isSetter) "set" else "get"
		val args = valueArg ?: ""
		val argTypes = if (isSetter) vType.toJson() else ""
		val ret = if (isSetter) TypeNode.Fqn("kotlin.Unit") else vType
		return """{"k":"callStatic","ownerType":$owner,"method":${str(name)},"prop":${str(kind)},"argTypes":[$argTypes],"ret":${ret.toJson()},"args":[$args]}"""
	}

	val readBody: String = when {
		hasExtRecv -> """{"k":"return","value":${extAccessorCall(false, null)}}"""
		externalStatic -> """{"k":"return","value":${externalStaticAccess(false, null)}}"""
		declClass != null && fieldBacked -> """{"k":"return","value":${fieldAccess(false, null)}}"""
		declClass == null -> {
			// Top-level property: mirrors the ordinary top-level property-read path (a plain val/var is a static
			// field; a computed one — no backing field — is a get_<name>() static).
			val owner = fileClassOf(prop)
			if (prop.backingField == null)
				"""{"k":"return","value":{"k":"callStatic","owner":${fqnJson(owner)},"method":${str("get_$name")},"args":[]}}"""
			else """{"k":"return","value":{"k":"staticField","ownerType":${fqnJson(owner)},"name":${str(name)}}}"""
		}
		else -> """{"k":"return","value":${accessorCall(false, null)}}"""
	}
	val readParams = if (unbound) """{"name":"receiver","type":${str(recvTypeNode ?: OBJ)}}""" else ""
	val getMethod = """{"name":"get","static":false,"override":true,"virtual":true,"params":[$readParams],"ret":${str(vType)},"body":[$readBody]}"""
	// KProperty0/KProperty1's declared supertype `() -> V`/`(T) -> V` gives them a REAL fake-overridden `invoke`
	// abstract member (confirmed in the compiled BIR: `interfaces` drops the FunctionN supertype — a Kotlin
	// function type has no faithful CLR interface base — but the interface's OWN `methods` still carries the
	// fake override AS ITS OWN abstract slot). So the lifted class must implement it too, same body as `get`
	// (mirrors JVM's `PropertyReferenceImpl.invoke() = get()`).
	val invokeMethod = """{"name":"invoke","static":false,"override":true,"virtual":true,"params":[$readParams],"ret":${str(vType)},"body":[$readBody]}"""

	val setMethod: String? = setterFn?.let {
		val setBody = when {
			hasExtRecv -> """{"k":"exprStmt","expr":${extAccessorCall(true, """{"k":"local","name":"value"}""")}}"""
			externalStatic -> """{"k":"exprStmt","expr":${externalStaticAccess(true, """{"k":"local","name":"value"}""")}}"""
			declClass != null && fieldBacked -> """{"k":"exprStmt","expr":${fieldAccess(true, """{"k":"local","name":"value"}""")}}"""
			declClass == null -> {
				val owner = fileClassOf(prop)
				if (prop.backingField == null)
					"""{"k":"exprStmt","expr":{"k":"callStatic","owner":${fqnJson(owner)},"method":${str("set_$name")},"args":[{"k":"local","name":"value"}]}}"""
				else """{"k":"exprStmt","expr":{"k":"staticFieldSet","ownerType":${fqnJson(owner)},"name":${str(name)},"value":{"k":"local","name":"value"}}}"""
			}
			else -> """{"k":"exprStmt","expr":${accessorCall(true, """{"k":"local","name":"value"}""")}}"""
		}
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

	val recvFieldType = if (bound) birType(capturedRecv!!.type) else null
	val fields = if (bound) """{"name":"__recv","type":${str(recvFieldType!!)}}""" else ""
	val ctorParams = if (bound) """{"name":"__recv","type":${str(recvFieldType!!)}}""" else ""
	val ctorBody = if (bound) """{"k":"setField","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":"__recv","value":{"k":"local","name":"__recv"}}""" else ""

	val freeTps = freeTypeParams(listOf(node.type) + listOfNotNull(capturedRecv?.type))
	val methods = listOfNotNull(getMethod, invokeMethod, setMethod).joinToString(",")
	// The lifted constructor delegates to ClrPropertyStub(String). Keep that Kotlin declaration identity beside
	// the arguments so bir2cir, which owns the CLR representation, can resolve and stamp the exact target ctor.
	val stubDelegationSig = """[{"t":"fqn","name":"kotlin.String"}]"""
	liftedTypes.add("""{"name":${str(cname)},"kind":"class","generated":true$companionCaptureTag${typeParamsJson(freeTps)},"base":${stubBase.toJson()},"interfaces":[${ifaceSpec.toJson()}],"fields":[$fields],"ctors":[{"params":[$ctorParams],"baseArgs":[$stubBaseArg],"delegationSig":$stubDelegationSig,"body":[$ctorBody]}],"methods":[$methods]}""")

	val classType = if (freeTps.isEmpty()) TypeNode.Fqn(cname) else TypeNode.Fqn(cname, freeTps.map { tvOf(it) })
	val ctorArgs = if (bound) expr(capturedRecv!!) else ""
	val ctorSig = if (bound) recvFieldType!!.toJson() else ""
	return """{"k":"new","type":${classType.toJson()},"argTypes":[$ctorSig],"args":[$ctorArgs]$companionCaptureTag}"""
}

/** Free value references in a lambda / local-fun body (referenced but not declared inside) = its captured vars. */
internal fun BirEmitter.capturedVars(fn: IrSimpleFunction, includeThis: Boolean = false): List<IrValueDeclaration> =
	captureScan(fn.body, fn.parameters, includeThis, newCycleGuard())

/**
 * A LOCAL declaration — a `fun` kotc lifts to a file-class static, or a class/object it lifts to a top-level
 * synthetic type. Both take their captures as leading by-value parameters supplied by the CALL / CONSTRUCTION site
 * ([liftLocalFn], [liftLocalClass]), so reaching one is itself a capture of everything it captures, which is why the
 * scans below must recognize it exactly.
 *
 * The frontend states the fact directly: a `fun`/class declared in statement position carries `Local` visibility (a
 * MEMBER of a local class does not — it keeps its declared visibility). Testing it is O(1) and touches no lazily
 * materialized member list — and unlike a test on the IR `parent`, it does not have to special-case `init { }`, where
 * the enclosing CLASS is the parent because `IrAnonymousInitializer` cannot be a declaration parent. It also matches a
 * class nested inside a local class, which is harmless here: such a class is only reachable through its local
 * enclosing one, whose captures the scan already collects.
 */
private fun isLocalDeclaration(d: IrDeclarationWithVisibility): Boolean =
	d.visibility.delegate == org.jetbrains.kotlin.descriptors.Visibilities.Local

private fun newCycleGuard(): MutableSet<IrElement> =
	java.util.Collections.newSetFromMap(java.util.IdentityHashMap<IrElement, Boolean>())

/**
 * The free value references under [body] — referenced anywhere inside, declared outside — given the declarations
 * [ownParams] that the scanned entity itself introduces. Shared by the lambda/local-fun scan ([capturedVars]) and the
 * object-literal / local-class scan ([capturedVarsForObject]) so both see the same capture set: they lift the same
 * kinds of body and must agree, or one of them emits a reference to a value its frame never received.
 *
 * Reaching a LOCAL declaration contributes its own captures (recursively): the lift passes them as leading arguments
 * at the reaching site, so a body that merely calls `bump()` — never mentioning `bump`'s `n` — must still have `n` in
 * scope, and likewise a body that only constructs a local class whose member writes `n`. [guard] holds the
 * declarations currently being scanned, so a cycle stops instead of recursing forever; the captures of a declaration
 * already on that stack are collected by the frame that entered it.
 */
private fun BirEmitter.captureScan(
	body: IrElement?,
	ownParams: List<IrValueDeclaration>,
	includeThis: Boolean,
	guard: MutableSet<IrElement>,
): List<IrValueDeclaration> {
	val declared = java.util.Collections.newSetFromMap(java.util.IdentityHashMap<IrValueDeclaration, Boolean>())
	ownParams.forEach { declared.add(it) }
	val referenced = LinkedHashSet<IrValueDeclaration>()
	fun reachLocalFunction(callee: IrSimpleFunction) {
		if (!isLocalDeclaration(callee) || !guard.add(callee)) return
		referenced.addAll(captureScan(callee.body, callee.parameters, true, guard))
		guard.remove(callee)
	}
	fun reachLocalClass(cls: IrClass) {
		if (!isLocalDeclaration(cls) || !guard.add(cls)) return
		referenced.addAll(captureScan(cls, emptyList(), true, guard))
		guard.remove(cls)
	}
	body?.acceptVoid(object : IrVisitorVoid() {
		override fun visitElement(element: IrElement) {
			when (element) {
				is IrVariable -> declared.add(element)
				// A nested lambda/local-fun's own parameters are declared there, not captured by this body.
				is IrValueParameter -> declared.add(element)
				is IrGetValue -> referenced.add(element.symbol.owner)
				is IrSetValue -> referenced.add(element.symbol.owner)
				// Ask a reached local declaration for `<this>` too: an enclosing-instance capture propagates the same
				// way, and this frame's own `includeThis` decides whether it survives the filter below.
				is IrCall -> (element.symbol.owner as? IrSimpleFunction)?.let(::reachLocalFunction)
				// A callable reference reaches the same declaration as a direct call/construction. The adapter
				// object emitted for `::localFun` / `::LocalClass` must receive the target's transitive captures,
				// so capture reachability is defined by the referenced declaration edge, not by invocation syntax.
				is IrFunctionReference -> when (val target = element.symbol.owner) {
					is IrSimpleFunction -> reachLocalFunction(target)
					is IrConstructor -> (target.parent as? IrClass)?.let(::reachLocalClass)
				}
				// CONSTRUCTING a local class / object expression is the same transitive capture one boundary over:
				// `liftLocalClass`/`blockExpr` prepend its captures as ctor arguments AT THIS SITE, so a body that only
				// does `L().go()` must hold whatever `L` captures. A DELEGATING construction (`class B : A()`, and an
				// object literal's `super()`) reaches a capturing base the same way — its captures are prepended to the
				// base call — so a derived local class must capture whatever its local base does.
				is IrConstructorCall, is IrDelegatingConstructorCall ->
					((element as IrFunctionAccessExpression).symbol.owner.parent as? IrClass)
						?.let(::reachLocalClass)
			}
			element.acceptChildrenVoid(this)
		}
	})
	return referenced.filter { it !in declared && (includeThis || it.name.asString() != "<this>") }
}

/**
 * Free outer values captured by an object literal or a local class: any value referenced anywhere in it
 * (method bodies + property initializers, and the captures of any local `fun` those call) but declared OUTSIDE it.
 * The anon's own receivers/params/locals are excluded by identity — crucially this keeps the captured enclosing
 * `this` (same name "<this>" as the anon's own receiver, distinguished only by symbol identity).
 */
internal fun BirEmitter.capturedVarsForObject(anon: IrClass): List<IrValueDeclaration> =
	captureScan(anon, emptyList(), includeThis = true, guard = newCycleGuard().also { it.add(anon) })

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

/**
 * Capture (decl, name) pairs, each name unique against [taken] and against the pairs already produced.
 *
 * A capture normally keeps its own Kotlin name — that is what every lift has always emitted, and what a reader
 * expects to see — unless something else already owns that name in the same namespace, in which case it moves into a
 * `cap$` prefix Kotlin source cannot spell. Set [alwaysPrefix] where the namespace cannot be enumerated up front:
 * a lifted local fun's captures become PARAMETERS, and ilemit resolves a `{k:local}` read against body locals before
 * parameters in one flat map (Emitter.Expressions/Bodies), so a body local declared anywhere inside the lift would
 * shadow a like-named capture parameter from that point on. A lifted CLASS has no such problem: its captures are
 * FIELDS, read through `this`, and the names they must avoid are exactly its own fields and constructor parameters.
 *
 * `__outer` is the preferred spelling for an enclosing `this`, but it is not a reserved user identifier. It therefore
 * goes through the same collision-free allocation as every other capture. Downstream consumers must use the emitted
 * field/parameter identity carried by the BIR use sites, never infer capture semantics from that preferred spelling.
 */
internal fun BirEmitter.uniqueCaptureNames(
	captured: List<IrValueDeclaration>,
	taken: MutableSet<String> = HashSet(),
	alwaysPrefix: Boolean = false,
): List<Pair<IrValueDeclaration, String>> {
	return captured.map { decl ->
		val name = captureFieldName(decl)
		val unique = generateSequence(if (alwaysPrefix) 1 else 0) { it + 1 }
			.map { if (it == 0) name else if (it == 1) "cap\$$name" else "cap\$$name\$${it - 1}" }
			.first { it !in taken }
		taken.add(unique)
		decl to unique
	}
}

/** [uniqueCaptureNames] for a lifted CLASS: its captures become fields beside the class's own fields, and leading
 *  parameters of every constructor beside that constructor's own parameters. */
internal fun BirEmitter.captureFieldPairs(
	klass: IrClass, captured: List<IrValueDeclaration>,
): List<Pair<IrValueDeclaration, String>> {
	val taken = HashSet<String>()
	klass.declarations.forEach { d ->
		when (d) {
			is IrProperty -> d.backingField?.let { taken.add(it.name.asString()) }
			is IrField -> taken.add(d.name.asString())
			is IrConstructor -> d.parameters.forEach { taken.add(it.name.asString()) }
			else -> {}
		}
	}
	return uniqueCaptureNames(captured, taken)
}

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
		?: if (d.name.asString() == "<this>") """{"k":"this"}""" else """{"k":"local","name":${str(localSlotName(d))}}"""

/**
 * The lambda's value parameters in DELEGATE (physical `FunctionN` type-argument) order — everything but the
 * dispatch receiver, in `IrFunction.parameters` order, which fir2ir already lays out as the physical sequence:
 * CONTEXT parameters, then the EXTENSION RECEIVER, then the regular params. `context(A) B.(D) -> E` is
 * `@ExtensionFunctionType Function3<A, B, D, E>` — the contexts come BEFORE the receiver, so the IR order IS the
 * delegate order and no re-sorting is needed. A receiver lambda `Scope.() -> Unit` is `Function1<Scope, Unit>`, so
 * its receiver is the first delegate argument (and the body's implicit-receiver refs resolve to it). Keeping this
 * consistent with `birType`'s view of the function type (which derives args from the FunctionN type arguments,
 * receiver and contexts alike) is what makes `build { ... }` receiver-lambda DSLs and context function types work.
 */
internal fun BirEmitter.orderedLambdaParams(fn: IrSimpleFunction): List<IrValueParameter> =
	fn.parameters.filter { it.kind != IrParameterKind.DispatchReceiver }

/** The function type `fn` for a lambda's Kotlin signature, in the physical delegate shape [orderedLambdaParams]
 *  defines. When the Kotlin type is an EXTENSION function type its FIRST physical argument rides `fn.recv` and the
 *  rest ride `fn.params` — that is what `birType` produces reading `@ExtensionFunctionType FunctionN<A1..An,R>`
 *  (recv = A1, params = A2..An), so a lambda's `funcType` and its target's declared type node are byte-identical.
 *  For `context(A) B.(D) -> E` that puts the CONTEXT in `recv` and the real receiver in `params[0]`: the labels
 *  diverge from Kotlin's roles, but the PHYSICAL projection (`recv` prepended to `params` by `DelegateParams`) is
 *  the correct `A, B, D` — and physical agreement is the whole contract of this node. A non-extension context
 *  function type (`context(A) (D) -> E` = `Function2<A, D, E>`) has no recv and carries `[A, D]` in params.
 *  A `suspend` lambda sets `fn.suspend=true`, carrying the suspend FACT for the SM builder. bir2cir ERASES a suspend
 *  `fn` to `object` wherever it appears in a TYPE slot; only the `funcType` node key itself keeps it. */
internal fun BirEmitter.funcTypeOf(fn: IrSimpleFunction): TypeNode.Fn {
	val physical = orderedLambdaParams(fn).map { birTypeDeleg(it.type) }
	val isExtType = fn.parameters.any { it.kind == IrParameterKind.ExtensionReceiver }
	val recv = if (isExtType) physical.firstOrNull() else null
	val ps = if (isExtType) physical.drop(1) else physical
	return TypeNode.Fn(fn.isSuspend, funcRetTypeOf(fn.returnType), ps, recv)
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

/** Lambda/closure method params with KProperty erased to Any (must agree with funcTypeOf for delegates), in the
 *  physical delegate order [orderedLambdaParams] defines: contexts, then the extension receiver (so a receiver
 *  lambda's `$this$build` is bound), then the regular params — i.e. IR order minus the dispatch receiver.
 *
 *  `recvName` renames the EXTENSION RECEIVER slot. A lambda's receiver parameter is the anonymous `<this>`, which is
 *  not an emittable identifier and which the body reads as `{k:this}` — meaningless in the STATIC lift / wrong in a
 *  closure `invoke`. Passing a minted name here and binding [BirEmitter.selfSubst] to it around the body emission is
 *  what makes the receiver reachable, exactly as `emitInlineLambdaCarrier` already does for a splice carrier. */
internal fun BirEmitter.lambdaParamsJson(params: List<IrValueParameter>, recvName: String? = null): String =
	params.filter { it.kind != IrParameterKind.DispatchReceiver }
		// A `Unit`-typed PARAMETER must be the real Unit VALUE identity, not `void` (invalid metadata).
		.joinToString(",") { p ->
			val ty = if (p.type.isUnit()) TypeNode.Fqn("kotlin.Unit") else birTypeDeleg(p.type)
			val nm = if (recvName != null && p.kind == IrParameterKind.ExtensionReceiver) recvName else p.name.asString()
			"""{"name":${str(nm)},"type":${ty.toJson()}}"""
		}

/** Emit `block` with a lifted lambda's OWN extension receiver bound to `recvName`, so the body's `this` reads render
 *  as that parameter instead of `{k:this}`. `{k:this}` is the ENCLOSING instance (or nothing at all in a static
 *  lift), so without this the receiver was never reachable: `val f: Int.(Int) -> Int = { d -> this + d }` threw a
 *  NullReferenceException, and once context parameters joined the physical sequence `this` began reading the CONTEXT
 *  — physical slot 0 — and silently returned the wrong value. Saved and RESTORED: the same parameter may already be
 *  bound by an enclosing carrier. */
internal inline fun <T> BirEmitter.withLambdaSelf(fn: IrSimpleFunction, recvName: String?, block: () -> T): T {
	val ext = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
	if (ext == null || recvName == null) return block()
	val had = selfSubst.containsKey(ext)
	val saved = selfSubst[ext]
	selfSubst[ext] = """{"k":"local","name":${str(recvName)}}"""
	try { return block() } finally { if (had) selfSubst[ext] = saved!! else selfSubst.remove(ext) }
}

/** A fresh name for a lifted lambda's extension-receiver parameter, or null when it has none. Allocated against the
 *  lambda's own frame ([BirEmitter.freshFrameName]) — `{ __recv0 -> this + __recv0 }` is legal Kotlin, and a bare
 *  counter would have emitted two parameters called `__recv0`. */
internal fun BirEmitter.lambdaRecvName(fn: IrSimpleFunction): String? =
	if (fn.parameters.any { it.kind == IrParameterKind.ExtensionReceiver }) freshFrameName("__recv", fn) else null

/** Lift a local function to a file-class static method; captured vars become leading params (by their own names). */
internal fun BirEmitter.liftLocalFn(fn: IrSimpleFunction) {
	// Captured vars (incl. the enclosing `this`) become leading params; the call site prepends their values.
	val captures = capturedVars(fn, includeThis = true)
	val lname = "__local${scopeCounter++}_${fn.name.asString()}"
	// A local fn's OWN value parameters = its regular params PLUS its dispatch/extension receiver. A callable-reference
	// ADAPTER (`ADAPTER_FOR_CALLABLE_REFERENCE`) whose bound receiver is an ExtensionReceiver param `receiver` references
	// that param by name in its body, so it MUST be emitted as a leading method param, in declaration order (receivers
	// precede regulars); filtering to Regular alone drops it and orphans a `receiver.f(p0)` body ref — the dangling-
	// `receiver` IrSanity fault (kotc consumes the RAW fir2ir form, before UpgradeCallableReferences would flatten
	// receivers to Regular). Captures are DISTINCT (outer decls, prepended before these). NOTE the still-unhandled
	// sibling: a genuine local EXTENSION fun `fun T.f()` names its receiver `<this>` and its body reads it as `{k:this}`
	// (exprInner), which is invalid in this STATIC lift — that needs a `__self`-style rename + selfSubst binding and is
	// out of scope here (it was equally broken before — the param was simply dropped).
	// The ORDER is the call site's: receivers (dispatch then extension) ahead of the [isValueParameter] sequence
	// (contexts then regulars), because the caller emits `capArgs + recvArgs + filledArgs(call)` in exactly that shape.
	// IR order would interleave a context parameter BETWEEN the two receivers (fir2ir orders dispatch/context/extension/
	// regular), which for a `context(c) fun T.f()` local would put the context value in the extension receiver's slot.
	val ownValueParams = fn.parameters.filter { it.kind == IrParameterKind.DispatchReceiver } +
		fn.parameters.filter { it.kind == IrParameterKind.ExtensionReceiver } +
		fn.parameters.filter { isValueParameter(it) }
	// A local fn referencing an enclosing type parameter (in a capture, its own params/receivers, its return, or a TYPE
	// OPERAND in its body such as `x is R` / `R::class`) becomes a GENERIC static method — reified CLR generics, same as
	// a capturing closure class, which scans `bodyTypeOperands` for exactly the same reason. The call site (callStatic)
	// passes the enclosing type params as type arguments. Missing a body-only operand here would also leave a `tv` this
	// lift cannot name in its own frame (see `_syntheticTypeArgs` below).
	val freeTps = freeTypeParams(
		captures.map { it.type } + ownValueParams.map { it.type } + listOf(fn.returnType) + bodyTypeOperands(fn))
	localFns[fn] = Triple(lname, captures, freeTps)
	fun pj(name: String, t: IrType) = """{"name":${str(name)},"type":${birType(t).toJson()}}"""
	// This lift puts the captures in the method's PARAMETER namespace, beside the fn's own value params — and a
	// transitive capture arrives from ANOTHER body, where its name may resolve to a different declaration than it does
	// here (`fun bump() { n++ }` called by `fun addTwice(n: Int)`). Uniquing keeps the wrong binding from happening.
	val capPairs = uniqueCaptureNames(
		captures, ownValueParams.mapTo(HashSet()) { it.name.asString() }, alwaysPrefix = true)
	// Captures arrive as leading params; rewrite body refs to those params. This must cover not only `<this>` but
	// also receiver-like captured params such as `$this$buildString`, otherwise an active inline substitution can
	// leak a caller-local (`__lam<N>`) into the lifted method body.
	// Save any prior substitution and RESTORE it after the body, exactly as the closure path does: this local fun may
	// be declared INSIDE a closure/object/local class that already bound the same outer decl to its own capture field.
	// Removing the binding instead would leave the enclosing frame reading the bare local again, so the `bump()` call
	// site after it (capValueExpr) emits a local that does not exist in that frame.
	val savedSubst = capPairs.associate { (decl, _) -> decl to captureSubst[decl] }
	capPairs.forEach { (decl, fname) -> captureSubst[decl] = """{"k":"local","name":${str(fname)}}""" }
	val capParams = capPairs.map { (decl, fname) -> """{"name":${str(fname)},"type":${str(captureFieldType(decl))}}""" }
	val ownParams = ownValueParams.map { pj(it.name.asString(), it.type) }
	val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
	capPairs.forEach { (decl, _) -> val prev = savedSubst[decl]; if (prev != null) captureSubst[decl] = prev else captureSubst.remove(decl) }
	val ret = birType(fn.returnType)
	// This lifted static RE-DECLARES the enclosing declaration's free type params as its OWN (`freeTps` ->
	// `typeParams`), but every `tv` token inside it still names them in their ORIGINAL frame (the enclosing class's
	// `tv{type,i}` / method's `tv{method,i}`) — the frame is part of a token's identity (#74). Carry the positional
	// correspondence so a consumer that must re-express one of those tokens in THIS method's frame can: bir2cir's
	// SharedSyntheticSynthesis needs it to construct a bare heap-ref-cell identity used here, whose element type is
	// registered in the enclosing frame. Same key, same meaning as the one ClosureSynthesis derives for a lifted
	// closure CLASS from `newClosure.typeArgs`; consumed and dropped there, never reaching CIR.
	//
	// The body is NOT re-expressed in this lift's frame. Doing that (a typeArgSubst over `freeTps`, the treatment
	// typeDef gives a lifted class) is the real fix for a body-local whose type is an enclosing variable, but it also
	// re-frames the `newClosure.typeArgs` of any closure nested in this body, from which ClosureSynthesis derives that
	// closure class's own correspondence — and the two derivations then disagree about which frame a cell inside the
	// closure belongs to. Attempted and reverted; the residual is bir2cir's loud refusal, recorded at bodyTypeOperands.
	val synthTvs = if (freeTps.isEmpty()) "" else ""","_syntheticTypeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
	// Lifting is a Kotlin-to-Kotlin structural projection. Preserve declaration facts exactly as for an ordinary
	// function; bir2cir remains solely responsible for lowering a suspend declaration to the CLR continuation ABI.
	liftedMethods.add("""{"name":${str(lname)},"generated":true,"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)}$synthTvs${funModsJson(fn)}${resultTypeJson(fn)},"params":[${(capParams + ownParams).joinToString(",")}],"ret":${str(ret)},"body":[$body]}""")
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
	// Writing a captured outer local from the class is heap ref-celled the SAME way the lambda/object path is: the
	// module-wide ref-cell scan (BirEmitter.initRefCells, run before ANY file is emitted) promoted every
	// captured-and-mutated `var` to a shared `dotkt$Ref<T>`, so `isRefCell(it)` is true here whatever root we are
	// under — a method, a constructor/init block, an initializer expression — and the class reads/writes the shared
	// cell. The shape is SUPPORTED; reaching the branch below means the scan and this predicate disagree (they read
	// the same two helpers over the same node), i.e. a mutated capture that is not a `var` local, which valid
	// frontend IR cannot produce: a Kotlin parameter cannot be assigned.
	if (captured.any { it in mutatedIn(klass) && !isRefCell(it) })
		return invariantBroken(klass, "a local class writes a captured outer variable that was not promoted to a " +
			"heap ref-cell")
	val capPairs = captureFieldPairs(klass, captured)
	// Save any prior binding and RESTORE it below rather than dropping it — this local class may be declared inside a
	// closure/object/local fun that already bound the same outer decl to its own capture field (mirrors the closure
	// path); dropping it leaves the enclosing frame reading a bare local it no longer has.
	val savedSubst = capPairs.associate { (decl, _) -> decl to captureSubst[decl] }
	capPairs.forEach { (decl, fname) ->
		captureSubst[decl] = """{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":${str(fname)}}"""
	}
	// A local class that references an enclosing type parameter (in a capture field, its supertype/bounds, or a member
	// signature/body operand) is lifted GENERICALLY over those params — reified CLR generics, exactly as the lifted
	// object-literal path (blockExpr) does. `captureEnclosingGenerics` runs typeDef's structural scan, which records
	// `liftedTypeArgParams[klass]`; each `new L()` site (IrConstructorCall) instantiates the flattened class with the
	// enclosing args from that record. A non-generic local class captures nothing and lifts unchanged.
	// Register BEFORE emitting the class: a member of this class may CONSTRUCT it (or a subclass may delegate to its
	// ctor) while `typeDef` is running, and that construction site reads this record to prepend the capture arguments.
	localClassCaptures[klass] = captured
	liftedTypes.add(typeDef(klass, capPairs, captureEnclosingGenerics = true, generated = true))
	capPairs.forEach { (decl, _) -> val prev = savedSubst[decl]; if (prev != null) captureSubst[decl] = prev else captureSubst.remove(decl) }
	return """{"k":"block","body":[]}"""
}
