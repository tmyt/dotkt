package kotc.backend

import kotc.bir.*
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

// `:` is forbidden even inside a Kotlin escaped identifier, but is valid in a CLR method name. Generated helpers
// therefore cannot collide with arbitrary source declarations; the counter only has to separate helpers from peers.
private fun BirEmitter.freshLiftedMethodName(kind: String): String = "dotkt:$kind:${lambdaCounter++}"

// Author a declaration/use binding at the same time as the lifted Kotlin body. The file-local allocation token
// is unique within this projection; downstream must carry this ID, never reconstruct it from a physical name.
private fun BirEmitter.liftedDeclarationIdentity(token: String): String = "dotkt-lifted-v1:$fileClass:$token"

private fun BirEmitter.liftedFunction(
	name: String, typeParameters: List<IrTypeParameter>, params: List<BirParameter>,
	ret: TypeNode, body: List<BirStatement>, semanticOwner: String? = null,
): BirFunctionDeclaration = BirFunctionDeclaration(name = name, declarationId = liftedDeclarationIdentity(name),
	generated = true, isStatic = true, isOverride = false, isVirtual = false,
	typeParams = typeParameterDeclarations(typeParameters).takeIf { typeParameters.isNotEmpty() },
	params = params, ret = ret, body = body, semanticOwner = semanticOwner)

private fun BirEmitter.liftedDelegate(
	name: String, functionType: TypeNode, typeArguments: List<TypeNode>?, owner: TypeNode = TypeNode.Fqn(fileClass),
): BirNewDelegate = BirNewDelegate(name, functionType, typeArgs = typeArguments,
	calleeOwner = owner, declarationId = liftedDeclarationIdentity(name))

/**
 * Collect the lexical type parameters used by a lifted declaration's signatures, captures, and body operands.
 * A same-module default is rendered in its caller's type scope: collect dependencies after that substitution,
 * just as birType renders the body, so the declaration and its construction edge describe the same frame.
 */
internal fun BirEmitter.freeTypeParams(types: List<IrType>): List<org.jetbrains.kotlin.ir.declarations.IrTypeParameter> {
	val acc = LinkedHashSet<org.jetbrains.kotlin.ir.declarations.IrTypeParameter>()
	fun walk(type: IrType, frame: DefaultTypeFrame) {
		fun walkClosed(t: IrType) {
			(t.classifierOrNull as? IrTypeParameterSymbol)?.let {
				// Bounds can introduce further dependencies, in the frame belonging to that declaration.
				if (acc.add(it.owner)) {
					val boundsFrame = frame.boundsFrame(it.owner)
					it.owner.superTypes.forEach { bound -> walk(bound, boundsFrame) }
				}
			}
			if (t is IrSimpleType) t.arguments.forEach { (it as? IrTypeProjection)?.type?.let(::walkClosed) }
		}
		walkClosed(frame.values?.invoke(type) ?: type)
	}
	types.forEach { walk(it, defaultTypeFrame) }
	return acc.toList()
}

/** Type arguments supplied when a synthetic declaration is materialized in the current lexical frame. An enclosing
 * lift may already have rebound the source parameter into its own class/method frame; bypassing that substitution
 * resurrects the old lexical token and leaves the construction naming a generic parameter that is no longer in scope. */
internal fun BirEmitter.liftedTypeArguments(parameters: List<IrTypeParameter>): List<TypeNode>? =
	parameters.takeIf { it.isNotEmpty() }?.map { typeArgSubst[it] ?: tvOf(it) }

/** Render a synthetic METHOD declaration in the fresh dense generic frame that declaration owns. The construction
 * edge is rendered before entering this helper via [liftedTypeArguments]; definition-side signatures, bodies, and
 * constraints must all be rendered inside it so no token from the enclosing type/method frame leaks into the lift. */
private inline fun <T> BirEmitter.withLiftedMethodFrame(
	parameters: List<IrTypeParameter>,
	block: () -> T,
): T {
	val saved = parameters.associateWith { typeArgSubst[it] }
	val origins = parameters.map { tvOf(it).let { token -> token.scope to token.i } }
	require(origins.distinct().size == origins.size) {
		"lifted method generic frame contains duplicate lexical type-parameter identities"
	}
	val savedOrigins = origins.associateWith { liftedTypeArgSubst[it] }
	parameters.forEachIndexed { index, parameter -> typeArgSubst[parameter] = TypeNode.Tv("method", index) }
	origins.forEachIndexed { index, origin -> liftedTypeArgSubst[origin] = TypeNode.Tv("method", index) }
	return try {
		block()
	} finally {
		parameters.forEach { parameter ->
			val previous = saved[parameter]
			if (previous != null) typeArgSubst[parameter] = previous else typeArgSubst.remove(parameter)
		}
		origins.forEach { origin ->
			val previous = savedOrigins[origin]
			if (previous != null) liftedTypeArgSubst[origin] = previous else liftedTypeArgSubst.remove(origin)
		}
	}
}

/** Close a lexical local declaration's re-declared type-parameter vector at one use site. Parameters owned by the
 * local function use the frontend-resolved type arguments from that call/reference; enclosing parameters retain
 * their current lexical substitution. The vector stays aligned with [BirEmitter.LocalFunctionFact.typeParams]. */
internal fun BirEmitter.localFunctionTypeArgs(
	fact: BirEmitter.LocalFunctionFact,
	function: IrSimpleFunction,
	resolved: List<IrType?>,
): List<TypeNode> = fact.typeParams.map { parameter ->
	val own = function.typeParameters.indexOfFirst { it === parameter }
	if (own >= 0) resolved.getOrNull(own)?.let { birType(it) }
		?: (typeArgSubst[parameter] ?: tvOf(parameter))
	else typeArgSubst[parameter] ?: tvOf(parameter)
}

/** Type operands USED in a function body (e.g. `x is R` / `x as R` / `R::class` / `check<R>(x)`).
 *  These are lexical type dependencies even when the body's parameters, return type and captured values do not
 *  mention R. Preserve them on a lifted declaration; bir2cir chooses their physical generic representation.
 *
 *  NOT included: the declared type of a body-LOCAL. Making the lift generic over it is not enough on its own — the
 *  lift's body would still name every `tv` in the ENCLOSING frame, which strict ilemit resolution correctly rejects.
 *  A local whose type is an enclosing variable therefore needs the whole body re-expressed in the lift's own frame (the
 *  treatment bir2cir's ClosureSynthesis.RebindSyntheticTypeVariables gives a lifted closure class). Adding the type
 *  here alone converts bir2cir's loud refusal into invalid IL, so the refusal stands until that lands. */
internal fun BirEmitter.bodyTypeOperands(fn: org.jetbrains.kotlin.ir.declarations.IrFunction): List<IrType> {
	val out = ArrayList<IrType>()
	fn.body?.acceptVoid(object : IrVisitorVoid() {
		override fun visitElement(element: org.jetbrains.kotlin.ir.IrElement) {
			// Don't descend into NESTED lambdas/local funs — each declaration computes its own free type parameters.
			if (element is IrFunctionExpression || element is org.jetbrains.kotlin.ir.declarations.IrFunction) return
			when (element) {
				is IrTypeOperatorCall -> out.add(element.typeOperand)
				is IrClassReference -> out.add(element.classType)
				is IrFunctionAccessExpression -> out.addAll(element.typeArguments.filterNotNull())
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
private fun BirEmitter.suspendLambda(node: IrFunctionExpression): BirNewSuspendLambda {
	val fn = node.function
	// Own params in delegate order (contexts, extension receiver, then regulars) — matches lambdaParameters + bir2cir's
	// create() views (arity 0/1 = fixed create() slots, arity >= 2 = create(args, completion)). arity = the count.
	val ownParams = orderedLambdaParams(fn)
	// Restricted-suspension builders (`sequence { }`/`iterator { }`'s @RestrictsSuspension SequenceScope receiver)
	// now flow through this SAME suspend-lambda path: bir2cir gives the lambda the `RestrictedSuspendLambda` base
	// (not the plain SuspendLambda), so the cold-core builder runs. No exclusion here — kotc emits the pure suspend
	// facts and bir2cir picks the restricted base from the receiver scope's @RestrictsSuspension annotation.
	val captures = capturedVars(fn, includeThis = true)
	// Every capture, including the enclosing receiver, gets a compiler-prefixed descriptor. `$` is not legal in a
	// Kotlin source identifier, so these names are disjoint from source-declared lambda params/locals. The allocator is
	// still seeded with the complete parameter frame because generated IR names are compiler input too. Body locals use
	// file-unique `dotkt$localN` slots. The receiver ROLE is an explicit `outer:true` fact; downstream must not infer it
	// from `__outer`.
	val outerCapture = captures.firstOrNull {
		(it as? IrValueParameter)?.kind == IrParameterKind.DispatchReceiver
	}
	val capturePairsByIdentity = java.util.IdentityHashMap<IrValueDeclaration, String>()
	uniqueCaptureNames(captures, ownParams.mapTo(HashSet()) { it.name.asString() }, alwaysPrefix = true)
		.forEach { (d, name) -> capturePairsByIdentity[d] = name }
	val capturePairs = captures.map { it to capturePairsByIdentity.getValue(it) }
	val captureDescriptors = capturePairs.map { (d, name) ->
		BirCaptureDescriptor(name, captureFieldType(d), outer = true.takeIf { d === outerCapture })
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
	val parameters = lambdaParameters(ownParams)
	val resultType = birType(fn.returnType)
	// Enclosing generic type params referenced by the SM (captures/params/return/body operands) -> open SM
	// instantiation. Declaration facts and construction arguments remain distinct frames.
	val freeTps = freeTypeParams(captures.map { it.type } + fn.parameters.map { it.type } + listOf(fn.returnType) + bodyTypeOperands(fn))
		.sortedWith(compareBy<IrTypeParameter>(
			{ if (tvOf(it).scope == "type") 0 else 1 },
			{ tvOf(it).i },
		))
	// One declaration frame carries names, variance, reification and bounds together.
	// The construction's typeArgs below belongs to the enclosing use-site frame.
	val typeParameters = typeParameterDeclarations(freeTps)
	val typeArguments = liftedTypeArguments(freeTps)
	val extensionReceiver = extensionReceiverParam(fn)
	// (B) body shadow: emit the SM body with each captured decl bound to its DESCRIPTOR name `{k:local,name:D}` in
	// `captureSubst` by declaration identity, so the body names the capture EXACTLY as the descriptor declares it (the
	// name bir2cir's spill rewrite keys on). Capture bindings take precedence over subject substitutions for that
	// declaration. Saved + restored around the emission, mirroring samConversion.
	// The explicit outer descriptor is represented in the body by ordinary `{k:this}`. In the lambda's OWN frame
	// bir2cir rewrites that spelling to the field named by `outer:true`. A member extension can capture a SECOND
	// `<this>` declaration; its ordinary descriptor stays an exact local spelling so the spill rewrite selects that
	// field rather than collapsing both receivers (#563).
	// Force these spellings after capValues was computed so an enclosing carrier/closure substitution remains solely a
	// CONSTRUCTION value and cannot leak a caller-frame token/local into the lambda body.
	val shadowCap = java.util.IdentityHashMap<IrValueDeclaration, BirExpression?>()
	val shadowCapLocalName = java.util.IdentityHashMap<IrValueDeclaration, String?>()
	for ((d, name) in capturePairs) {
		shadowCap[d] = captureSubst[d]
		shadowCapLocalName[d] = captureLocalName[d]
		val isOuterReceiver = d === outerCapture
		captureSubst[d] = if (isOuterReceiver)
			// The body spelling is owner-relative, but its exact static type is already known here. Carry that frontend
			// fact on the value node so a later inline splice can move it into an array operation without asking
			// bir2cir to reconstruct which receiver frame the bare `this` originally denoted (#558).
			kotc.bir.BirThis(sty = birType(d.type))
		else
			BirLocal(name)
		if (isOuterReceiver) captureLocalName.remove(d) else captureLocalName[d] = name
	}
	// A suspend extension lambda has two distinct `this` candidates: its own extension receiver and a captured
	// enclosing dispatch receiver. Preserve that distinction in BIR instead of asking bir2cir to infer it from a bare
	// `this`: the extension receiver is the leading physical lambda param, so bind its IrGetValue to that param by
	// identity while emitting the body. A remaining bare `this` can then only denote the captured enclosing instance.
	val hadExtensionReceiver = extensionReceiver != null && selfSubst.containsKey(extensionReceiver)
	val savedExtensionReceiver = extensionReceiver?.let { selfSubst[it] }
	if (extensionReceiver != null)
		selfSubst[extensionReceiver] = BirLocal(extensionReceiver.name.asString())
	val body = withFreshLambdaLocalFunctionIds(fn) {
		(fn.body as? IrBlockBody)?.statements.orEmpty().flatMap { stmt(it) }
	}
	if (extensionReceiver != null) {
		if (hadExtensionReceiver) selfSubst[extensionReceiver] = savedExtensionReceiver!!
		else selfSubst.remove(extensionReceiver)
	}
	shadowCap.forEach { (d, prev) -> if (prev != null) captureSubst[d] = prev else captureSubst.remove(d) }
	shadowCapLocalName.forEach { (d, prev) -> if (prev != null) captureLocalName[d] = prev else captureLocalName.remove(d) }
	return BirNewSuspendLambda(arity = ownParams.size, captures = captureDescriptors,
		capValues = capValueSlots.takeIf { it.isNotEmpty() }, params = parameters, suspendRet = resultType,
		typeParams = typeParameters, typeArgs = typeArguments, body = body, funcType = funcTypeOf(fn))
}

/** Every emitted lambda/adapter body is a distinct BIR declaration projection, even when the frontend reuses the
 * same IR function for a default carrier and its in-module executable form. Give local declarations in that body a
 * fresh identity as one atomic projection; callers still supply the surrounding capture/parameter substitutions. */
private inline fun <T> BirEmitter.withFreshLambdaLocalFunctionIds(fn: IrSimpleFunction, block: () -> T): T =
	fn.body?.let { withClonedLocalFunctionIds(it, block) } ?: block()

internal fun BirEmitter.lambda(node: IrFunctionExpression): BirExpression {
	val fn = node.function
	// A `suspend` lambda LITERAL -> a `newSuspendLambda` node: bir2cir turns it into a SuspendLambda state machine
	// (app-build only; the SM's create/resume protocol makes `blockOn { ... }` run). kotc emits only the pure FACTS
	// (captures/params/body-with-suspendCall-tags); the SM lowering is downstream. Any arity N flows through
	// suspendLambda now; restricted-suspension builders (sequence{}/iterator{}) go through it too — bir2cir gives
	// them the RestrictedSuspendLambda base.
	if (fn.isSuspend) return suspendLambda(node)
	// kotc does NO coroutine lowering: a `suspend () -> T` lambda emits as a PLAIN lambda (its suspend calls carry
	// `"suspendCall":true`); the Task-ABI / state-machine lowering is a deferred downstream layer. So the declared
	// return / delegate type stay the plain Kotlin shapes here.
	val ret = birType(fn.returnType)
	val ftype = funcTypeOf(fn)
	// A lambda has no `this` of its own, so a referenced `<this>` is the enclosing instance -> capture it.
	val captures = capturedVars(fn, includeThis = true)
	if (captures.isEmpty()) {
		val lname = freshLiftedMethodName("lambda")
		val freeTps = freeTypeParams(fn.parameters.map { it.type } + listOf(fn.returnType) + bodyTypeOperands(fn))
		val typeArgs = liftedTypeArguments(freeTps)
		val semanticOwner = activeSemanticOwner ?: fileClass
		val declaration = withLiftedMethodFrame(freeTps) {
			val recvName = lambdaRecvName(fn)
			val body = withFreshLambdaLocalFunctionIds(fn) {
				withLambdaSelf(fn, recvName) {
					(fn.body as? IrBlockBody)?.statements.orEmpty().flatMap { stmt(it) }
				}
			}
			// The frontend may represent a non-capturing lambda as a file-level helper, but its declaration still belongs
			// to the surrounding Kotlin type. Preserve that owner explicitly: bir2cir decides whether/how it nests the
			// helper, and nested declarations created in this body retain the owner's generic frame and constraints.
			liftedFunction(lname, freeTps, lambdaParameters(fn.parameters, recvName).map { BirParameter(it.name, it.type) },
				birType(fn.returnType), body, semanticOwner)
		}
		liftedMethods.add(declaration)
		return BirNewDelegate(method = lname, declarationId = liftedDeclarationIdentity(lname), funcType = ftype,
			typeArgs = typeArgs, calleeOwner = semanticUseSiteOwnerSpec(fn))
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
		captureSubst[decl] = BirFieldGet(TypeNode.Fqn(cname), BirThis(), fname, outer = true.takeIf { isExactOuterDeclaration(decl) })
	}
	val recvName = lambdaRecvName(fn)
	val body = withFreshLambdaLocalFunctionIds(fn) {
		withLambdaSelf(fn, recvName) {
			(fn.body as? IrBlockBody)?.statements.orEmpty().flatMap { stmt(it) }
		}
	}
	capPairs.forEach { (decl, _) -> val prev = savedSubst[decl]; if (prev != null) captureSubst[decl] = prev else captureSubst.remove(decl) }
	val fields = capPairs.map { (decl, fname) ->
		BirCaptureDescriptor(fname, captureFieldType(decl), outer = true.takeIf { isExactOuterDeclaration(decl) })
	}
	// The closure must be GENERIC over any enclosing type parameters it captures (reified CLR generics — a `gp:T`
	// field is unresolved otherwise). Declare them on the class and pass them as type arguments at `newClosure`.
	val freeTps = freeTypeParams(capPairs.map { it.first.type } + fn.parameters.map { it.type } + listOf(fn.returnType) + bodyTypeOperands(fn))
	// #52 (kotc-purity): kotc no longer SYNTHESIZES the closure CLASS (a CLR-representation type). It emits the raw
	// build-INGREDIENTS as a transient `synthClass` fact (capture fields=name+type, invoke params/ret/body, generic
	// type-param decls); bir2cir's ClosureSynthesis assembles the actual closure class (kind/base/interfaces wrapper +
	// the ctor field-init body) into the file `types`, then STRIPS `synthClass`, leaving the canonical CIR `newClosure`
	// contract (closureType + capture VALUE exprs + funcType + typeArgs) that ilemit consumes one-to-one.
	val synthClass = BirClosureDefinition(cname, fields, lambdaParameters(fn.parameters, recvName), ret, body,
		typeParams = typeParameterDeclarations(freeTps).takeIf { freeTps.isNotEmpty() }, semanticOwner = activeSemanticOwner ?: fileClass)
	// Capture values are evaluated in the enclosing context (the outer `this`, or an outer local).
	val capExprs = captures.map { capValueExpr(it) }
	val typeArgs = liftedTypeArguments(freeTps)
	return BirNewClosure(TypeNode.Fqn(cname), capExprs, "invoke", ftype, synthClass, typeArgs = typeArgs)
}

/**
 * SAM conversion `Comparator { a, b -> … }` -> a synthetic class that IMPLEMENTS the fun interface (the SAM method =
 * the lambda body) and is instantiated via `newSam`. Unlike a function-type lambda (which lowers to a Func delegate),
 * a fun-interface value is used by INTERFACE (`comparator.compare(...)`), so a delegate has no matching method
 * (EntryPointNotFound). This mirrors the closure-class build but implements the iface + names the method after the SAM
 * + override:true, and returns the instance itself (not a delegate). Reuses the working object:Comparator emission.
 */
private fun BirEmitter.samOperandFunctionType(type: IrType): TypeNode.Fn {
	val visited = HashSet<org.jetbrains.kotlin.ir.symbols.IrClassifierSymbol>()
	fun find(current: IrType): TypeNode.Fn? {
		(birType(current.makeNotNull()) as? TypeNode.Fn)?.let { return it }
		val classifier = current.classifierOrNull ?: return null
		if (!visited.add(classifier)) return null
		return when (val declaration = classifier.owner) {
			is IrTypeParameter -> declaration.superTypes.firstNotNullOfOrNull(::find)
			is IrClass -> {
				val actuals = (current as? IrSimpleType)?.arguments.orEmpty()
				val substitution = org.jetbrains.kotlin.ir.types.IrTypeSubstitutor(
					declaration.typeParameters.map { it.symbol }, actuals, true)
				declaration.superTypes.firstNotNullOfOrNull { find(substitution.substitute(it)) }
			}
			else -> null
		}
	}
	return find(type) ?: error("SAM operand has no Kotlin function supertype")
}

internal fun BirEmitter.samConversion(node: IrTypeOperatorCall): BirExpression {
	val funIface = node.typeOperand
	val ifaceClass = funIface.classifierOrNull?.owner as? IrClass ?: return expr(node.argument)
	val lamExpr = node.argument as? IrFunctionExpression
	if (lamExpr == null) {
		val value = expr(node.argument)   // callable reference / existing implementation
		if (!isExternalNetType(ifaceClass)) return value
		// The conversion owns its operand: inline substitution and evaluation-plan lowering may replace the
		// operand without replacing the surrounding Kotlin SAM operation. CLR realization belongs to bir2cir.
		return BirSamConversion(birType(funIface), samOperandFunctionType(node.argument.type), value)
	}
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
	val savedSubst = java.util.IdentityHashMap<IrValueDeclaration, BirExpression?>()
	capPairs.forEach { (decl, fname) ->
		savedSubst[decl] = captureSubst[decl]
		captureSubst[decl] = BirFieldGet(TypeNode.Fqn(cname), BirThis(), fname, outer = true.takeIf { isExactOuterDeclaration(decl) })
	}
	val samParams = lambdaParameters(fn.parameters).map { BirParameter(it.name, it.type) }
	val body = withFreshLambdaLocalFunctionIds(fn) {
		(fn.body as? IrBlockBody)?.statements.orEmpty().flatMap { stmt(it) }
	}
	// A `suspend fun interface` (e.g. FlowCollector { emit(...) }) — the SAM method carries the pure suspend FACT via
	// `mods.suspend`, the discriminator bir2cir's SuspendColdLowering.IsMemberShapeEligible gates on. Without it a
	// suspend SAM `emit` body keeps its raw `suspendCall`-tagged nodes (never cold-transformed) — the whole flow
	// operator surface (the FlowCollector{} SAM emits: Combine/Merge/Transform/…) would dangle in ilemit. kotc reads
	// only `sam.isSuspend` (a pure Kotlin fact); the coroutine ABI lowering is entirely downstream.
	// `suspendRet` rides ALONGSIDE `mods.suspend` (the same pairing as every other declaration
	// emitter): the modifier is the FACT, the slot is the Kotlin RESULT TYPE, and bir2cir's cold registry reads the
	// slot — a declaration carrying the modifier without it has had its result type dropped, which cost the suspend
	// SAM's awaited values their type. Same value as `ret` here, since `ret` is the lambda's own Kotlin return type.
	val samMods = if (sam.isSuspend) BirModifiers(isSuspend = true) else null
	// Preserve a projected CLR classifier's exact TypeDef identity, including nested owners and arity collisions.
	// The CLR realization of the SAM is still decided by bir2cir.
	val ifaceSpec = if (isExternalNetType(ifaceClass)) birType(funIface)
		else ownerSpec(ifaceClass, funIface) ?: birType(funIface)
	// A SAM body implements the selected Kotlin declaration just like an authored override. Preserve that
	// relation independently of its substituted signature; bir2cir owns any physical slot adaptation.
	val samOverride = BirOverride(ifaceSpec, samName, "method", emittedParamCount(sam), declarationId = declarationIdForPhysicalAllocation(sam))
	val samMethod = BirFunctionDeclaration(name = samName, isStatic = false, isOverride = true, isVirtual = true,
		params = samParams, ret = ret, mods = samMods, suspendRet = ret.takeIf { sam.isSuspend }, overrides = listOf(samOverride), body = body)
	savedSubst.forEach { (decl, prev) -> if (prev != null) captureSubst[decl] = prev else captureSubst.remove(decl) }
	val fields = capPairs.map { (decl, fname) ->
		BirFieldDeclaration(fname, captureFieldType(decl), outer = true.takeIf { isExactOuterDeclaration(decl) })
	}
	val ctorBody = capPairs.map { (decl, fname) ->
		val outer = true.takeIf { isExactOuterDeclaration(decl) }
		BirSetField(TypeNode.Fqn(cname), BirThis(), fname, BirLocal(fname, outer = outer), outer = outer)
	}
	val freeTps = freeTypeParams(listOf(funIface) + capPairs.map { it.first.type } + fn.parameters.map { it.type } + listOf(fn.returnType) + bodyTypeOperands(fn))
	// #52/#75: the SAM shim class travels as a `synthClass` FACT ON the `newSam` node — NOT `liftedTypes.add`'d as a
	// sibling type. A sibling type stays in the ORIGIN file; when this `newSam` rides in an inline fn's [KotlinInline]
	// payload and is spliced into a CONSUMING file (cross-module compareBy/comparator), only the fn body travels, so the
	// SAM class must be self-carried on the node. bir2cir's ClosureSynthesis assembles it into the consuming file (with
	// name-dedup) and strips `synthClass` — exactly as it does for `lambda()`'s `newClosure` synthClass. Same-file (no
	// splice): ClosureSynthesis synthesizes the SAME class into this file, so a non-spliced `newSam` is unchanged.
	val synthClass = BirClassDefinition(name = cname, generated = true, base = null, typeParams = typeParameterDeclarations(freeTps).takeIf { freeTps.isNotEmpty() },
		semanticOwner = activeSemanticOwner ?: fileClass, interfaces = listOf(ifaceSpec), fields = fields,
		ctors = listOf(BirConstructor(fields.map { BirParameter(it.name, it.type, outer = it.outer) }, null, ctorBody)), methods = listOf(samMethod))
	val capExprs = captures.map { capValueExpr(it) }
	val tArgs = liftedTypeArguments(freeTps)
	return BirNewSam(TypeNode.Fqn(cname), capExprs, synthClass, typeArgs = tArgs)
}

/**
 * A callable reference `::foo` -> a delegate bound to a compiler-owned adapter for the referenced function. Handled:
 * a top-level/static function reference (no receiver — a lifted static forwarder), a
 * constructor reference (`::Ctor`/`::NetType` -> a lifted static factory), a bound-instance reference
 * (`obj::method` -> a receiver-capturing closure), an UNBOUND member reference (`Class::method` -> a lifted static
 * `__mref(self, args)`), a .NET method reference (bound/unbound), and — G8 — an UNBOUND top-level
 * EXTENSION-function reference (`String::isNotBlank`, `Type::extFn`): a lifted static forwarder whose BODY is the
 * faithful extension call (the `callStatic owner:null` shape the direct top-level ext-call path emits in `call()`),
 * bound as a delegate. The forwarder is
 * needed (not a bare `ldftn`) because a @ClrIntrinsic stdlib ext has no real rt.dll body (bir2cir substitutes it
 * to a BCL call) — the CALL node gives bir2cir something to substitute. (Plain @InlineOnly funs without @ClrIntrinsic
 * DO get real rt.dll bodies; @ClrIntrinsic is the body-removing discriminator.) Bound extension references capture
 * their eagerly evaluated receiver in an adapter rather than closing a static delegate. Suspend references do not
 * take any of these delegate paths:
 * [suspendFunctionRef] uniformly adapts top-level/member/extension references to `newSuspendLambda`.
 */
internal fun BirEmitter.functionRef(node: IrFunctionReference): BirExpression {
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
				val fields = capPairs.map { (decl, fname) ->
					BirCaptureDescriptor(fname, captureFieldType(decl))
				}
				val ctorArgs = (capPairs.map { (_, fname) ->
					kotc.bir.BirFieldGet(ownerType = TypeNode.Fqn(cname), recv = kotc.bir.BirThis(), name = fname)
				} + ps.map { BirLocal(it.name.asString()) })
				val ctorArgTypes = capPairs.map { (decl, _) -> captureFieldType(decl) } + ps.map { birType(it.type) }
				val retT = birType(ctor.returnType)
				val newE = kotc.bir.BirNew(type = ownerSpec(klass, ctor.returnType), argTypes = ctorArgTypes, args = ctorArgs)
				val freeTps = freeTypeParams(
					captures.map { it.type } + ps.map { it.type } + listOf(ctor.returnType))
				val synthClass = BirClosureDefinition(cname, fields, lambdaParameters(ps), retT, listOf(BirReturn(newE)),
					typeParams = typeParameterDeclarations(freeTps).takeIf { freeTps.isNotEmpty() }, semanticOwner = activeSemanticOwner ?: fileClass)
				val capExprs = captures.map { capValueExpr(it) }
				val typeArgs = liftedTypeArguments(freeTps)
				return BirNewClosure(TypeNode.Fqn(cname), capExprs, "invoke", TypeNode.Fn(false, retT, ps.map { birTypeDeleg(it.type) }),
					synthClass, typeArgs = typeArgs)
			}
			val lname = freshLiftedMethodName("ctorref")
			val retT = birType(ctor.returnType)
			val freeTps = freeTypeParams(ps.map { it.type } + listOf(ctor.returnType))
			val typeArgs = liftedTypeArguments(freeTps)
			val declaration = withLiftedMethodFrame(freeTps) {
				val parameters = ps.map { BirParameter(it.name.asString(), birType(it.type)) }
				val arguments = ps.map { BirLocal(it.name.asString()) }
				val argTypes = ps.map { birType(it.type) }
				val liftedRet = birType(ctor.returnType)
				val newE = BirNew(type = ownerSpec(klass, ctor.returnType), argTypes = argTypes, args = arguments)
				liftedFunction(lname, freeTps, parameters, liftedRet, listOf(BirReturn(newE)))
			}
			liftedMethods.add(declaration)
			return liftedDelegate(lname, TypeNode.Fn(false, retT, ps.map { birTypeDeleg(it.type) }), typeArgs)
		}
		// `::NetType` — a lifted factory `__ctorref(args) = new NetType(args)`, bound as a delegate. kotc emits a
		// plain `new` carrying the .NET-FQN identity; bir2cir TransformNew reshapes it to `newClr` off the refs.
		if (klass != null) {
			val ps = ctor.parameters.filter { it.kind == IrParameterKind.Regular }
			val lname = freshLiftedMethodName("ctorref")
			val retT = birType(ctor.returnType)
			val freeTps = freeTypeParams(ps.map { it.type } + listOf(ctor.returnType))
			val typeArgs = liftedTypeArguments(freeTps)
			val declaration = withLiftedMethodFrame(freeTps) {
				val parameters = ps.map { BirParameter(it.name.asString(), birType(it.type)) }
				val arguments = ps.map { BirLocal(it.name.asString()) }
				val liftedRet = birType(ctor.returnType)
				val newE = BirNew(type = TypeNode.Fqn(clrName(klass)!!), argTypes = ps.map { birType(it.type) }, args = arguments)
				liftedFunction(lname, freeTps, parameters, liftedRet, listOf(BirReturn(newE)))
			}
			liftedMethods.add(declaration)
			return liftedDelegate(lname, TypeNode.Fn(false, retT, ps.map { birTypeDeleg(it.type) }), typeArgs)
		}
		return unsupported(node, "this constructor reference", "the constructor's class could not be resolved")
	}
	val fn = node.symbol.owner as? IrSimpleFunction
		?: return unsupported(node, "this function reference", "only references to plain (simple) functions are supported")
	val selectedDeclaration = kotc.frontend.SelectedCallDeclarations.at(
		sourcePathOf(node), node.endOffset, fn.name.asString(), reference = true,
	)
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
	val hasExt = (extensionReceiverParam(fn) != null)
	val ownerClass = fn.parent as? IrClass
	// The callable-reference symbol may be a use-site-substituted wrapper. Its reflection target is the
	// frontend-selected declaration and is therefore the authoritative source of the identity. Keep using `fn`
	// for the substituted callable shape below; only the declaration fingerprint comes from here. Unlike a MethodDef
	// emission decision, a use site always fingerprints the selected Kotlin declaration; bir2cir decides whether that
	// identity has a physical carrier in the current/reference module.
	val identityDeclaration = selectedDeclaration ?: (node.reflectionTarget?.owner as? IrSimpleFunction) ?: fn
	val selectedDeclarationId = declarationIdForPhysicalAllocation(identityDeclaration)
	val physicalIdentityDeclaration = if (identityDeclaration.isFakeOverride && selectedDeclarationId == null)
		identityDeclaration.resolveFakeOverride() ?: identityDeclaration else identityDeclaration
	// Kotlin static/companion-block references expose the semantic companion member as reflectionTarget, while the
	// receiverless callable symbol is the class-owned declaration that kotc actually emits. The former deliberately
	// has no independent CLR allocation; use the emitted declaration's identity in that representation only.
	val memberDeclarationIdentity = selectedDeclarationId
		?: declarationIdForPhysicalAllocation(physicalIdentityDeclaration) ?: declarationIdForPhysicalAllocation(fn)
	val flagsOperation = clrFlagsOperation(fn)
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
	// `::localFun` — a reference to a lexical declaration by its BIR id. Captures are explicit leading parameters;
	// bir2cir chooses the eventual static MethodDef owner and compiler-reserved name.
	localFns[fn]?.let { local ->
		return localFunctionRef(node, fn, local, resolvedFuncType)
	}
	// A STATIC member reference — `C::f` for a Kotlin 2.4 `companion { fun f() }`, or a direct static declaration
	// loaded from a CLR reference KLIB. Neither has a dispatch receiver, and neither is a top-level file-facade
	// function, so the plain `newDelegate` below would lose the declaring type entirely. Materialize an adapter
	// owned by the current Kotlin declaration: protected access depends on that lexical family. Its body carries
	// the target's declaring-owner identity and static fact; bir2cir selects both methods' physical owners.
	if (dispatchIdx < 0 && !hasExt && ownerClass != null &&
		(isExternalNetType(ownerClass) || isKotlinStaticFunction(fn))) {
		val clrOwner = (if (isExternalNetType(ownerClass)) clrName(ownerClass) else typeName(ownerClass))
			?: return unsupported(node, "this static function reference", "its declaring owner identity is missing")
		val regs = fn.parameters.filter { it.kind == IrParameterKind.Regular }
		if (regs.size != resolvedFuncType.params.size)
			return unsupported(node, "this static function reference",
				"its use-site function arity does not match the resolved declaration")
		val lname = freshLiftedMethodName("mref")
		val anySlot = true.takeIf { isAnySlotMethod(fn) }
		val freeTps = freeTypeParams(listOf(node.type))
		val adapterTypeArgs = liftedTypeArguments(freeTps)
		val semanticOwner = activeSemanticOwner ?: fileClass
		val adapterOwner = semanticUseSiteOwnerSpec()
		val declaration = withLiftedMethodFrame(freeTps) {
			val liftedFuncType = birType(node.type) as TypeNode.Fn
			val parameters = regs.zip(liftedFuncType.params).map { (p, t) ->
				BirParameter(p.name.asString(), t)
			}
			val arguments = regs.map { BirLocal(it.name.asString()) }
			val argTypes = regs.map { birType(it.type) }
			val liftedReferenceTypeArgs = functionReferenceTypeArgs(node, fn)
				?: error("validated static function reference lost its type arguments in its lifted method frame")
			val selectedOwner = kotc.frontend.ClrStaticOwners.at(sourcePathOf(node), node.endOffset, fn.name.asString(), "call")
			val ownerType = selectedOwner?.let(::birType) ?: TypeNode.Fqn(clrOwner)
			val callE = BirCallStatic(ownerType = ownerType, method = fn.name.asString(), sig = overloadSignature(fn),
				typeArgs = liftedReferenceTypeArgs.takeIf { it.isNotEmpty() }, argTypes = argTypes, ret = liftedFuncType.ret,
				args = arguments, anySlot = anySlot, declarationId = memberDeclarationIdentity)
			val body = if (fn.returnType.isUnit()) BirExpressionStatement(callE) else BirReturn(callE)
			liftedFunction(lname, freeTps, parameters, liftedFuncType.ret, listOf(body), semanticOwner)
		}
		liftedMethods.add(declaration)
		return liftedDelegate(lname, resolvedFuncType, adapterTypeArgs, adapterOwner)
	}
	if (dispatchIdx < 0 && !hasExt) {
		val targetName = fn.name.asString()
		val companionReceiver = companionReceiverCallType(fn, node)
		// The companion receiver is an association fact, not overload identity. Keep both: bir2cir first uses the
		// association to choose the physical extension container, then uses the FIR-selected declaration identity to
		// bind the exact MethodDef when two overloads erase to the same CLR signature.
		val declarationIdentity = declarationIdForPhysicalAllocation(fn)
		val refFuncType = birType(node.type) as? TypeNode.Fn
			?: return unsupported(node, "this top-level function reference",
				"its FIR-resolved use-site type was not a function type")
		if (companionReceiver == null || fn.typeParameters.isNotEmpty()) {
			// Bind an ordinary top-level reference through a compiler-owned adapter. The reference may later flow into an
			// open function slot whose nullable type variable is physically `object`; bir2cir can move this generated
			// target with the delegate without rewriting the referenced declaration. The same adapter closes a generic
			// target through an explicit MethodSpec on its inner call. A non-generic companion-associated reference keeps
			// the direct form below because CompanionRepresentationLowering owns that physical association.
			val valueParams = regularParams(fn)
			if (valueParams.size != refFuncType.params.size)
				return unsupported(node, "this top-level function reference",
					"its use-site function arity does not match the resolved declaration")
			val lname = freshLiftedMethodName("fnref")
			val freeTps = freeTypeParams(listOf(node.type))
			val adapterTypeArgs = liftedTypeArguments(freeTps)
			val declaration = withLiftedMethodFrame(freeTps) {
				val liftedFuncType = birType(node.type) as TypeNode.Fn
				val parameters = valueParams.zip(liftedFuncType.params).map { (p, t) ->
					BirParameter(p.name.asString(), t)
				}
				val arguments = valueParams.map { BirLocal(it.name.asString()) }
				val liftedRefTa = functionReferenceTypeArgs(node, fn)
					?: error("validated top-level function reference lost its type arguments in its lifted method frame")
				val call = BirCallStatic(owner = BirField.Null, method = targetName, sig = overloadSignature(fn),
					typeArgs = liftedRefTa.takeIf { it.isNotEmpty() }, args = arguments, ret = liftedFuncType.ret,
					calleeOwner = calleeOwnerType(fn), companionReceiver = companionReceiver, declarationId = declarationIdentity)
				val body = if (liftedFuncType.ret == TypeNode.Fqn("kotlin.Unit")) BirExpressionStatement(call) else BirReturn(call)
				liftedFunction(lname, freeTps, parameters, liftedFuncType.ret, listOf(body))
			}
			liftedMethods.add(declaration)
			return liftedDelegate(lname, refFuncType, adapterTypeArgs)
		}
		return BirNewDelegate(targetName, refFuncType, sig = overloadSignature(fn), calleeOwner = calleeOwnerType(fn),
			companionReceiver = companionReceiver, declarationId = declarationIdentity)
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
		if (fnType.params.isEmpty())
			return unsupported(node, "this extension-function reference",
				"the reference's inferred type carries no receiver parameter")
		val lname = freshLiftedMethodName("mref")
		// freeTypeParams over node.type's SUBSTITUTED args (not the declared fn params — same call-site-type trap):
		// picks up only genuine ENCLOSING-context type vars (a `fun <E> …` scope), never the ext fn's OWN T (already
		// substituted away in node.type). The lifted static must be generic over those enclosing vars.
		val nodeTypeArgs = (node.type as? IrSimpleType)?.arguments?.mapNotNull { (it as? IrTypeProjection)?.type }.orEmpty()
		val freeTps = freeTypeParams(nodeTypeArgs)
		val typeArgs = liftedTypeArguments(freeTps)
		// A lifted declaration owns an independent METHOD-generic frame. Render every declaration occurrence in that
		// frame; retaining the enclosing declaration's `type`/`method` scope here produces CIR that can only be emitted by
		// guessing across generic-parameter pools. [fnType]/[typeArgs] remain in the enclosing frame because the delegate
		// construction below is a use-site operand, not part of the lifted declaration.
		val declaration = withLiftedMethodFrame(freeTps) {
			val liftedFnType = birType(node.type) as TypeNode.Fn
			val selfT = liftedFnType.params.first()
			val regTypes = liftedFnType.params.drop(1)
			val parameters = listOf(BirParameter("__self", selfT)) +
				regTypes.mapIndexed { i, t -> BirParameter("__a${i + 1}", t) }
			val callArgs = (listOf(kotc.bir.BirLocal(name = "__self")) +
				regTypes.indices.map { BirLocal("__a${it + 1}") })
			val retT = liftedFnType.ret
			val retVoid = retT == TypeNode.Fqn("kotlin.Unit") // the SUBSTITUTED return (fn's own T may resolve to Unit)
			val callE = extensionReferenceCall(node, fn, retT, callArgs)
			val body = if (retVoid) BirExpressionStatement(callE) else BirReturn(callE)
			liftedFunction(lname, freeTps, parameters, retT, listOf(body))
		}
		liftedMethods.add(declaration)
		return liftedDelegate(lname, fnType, typeArgs)
	}
	// `obj::method` captures its eagerly evaluated receiver in a compiler-owned closure. If this reference later
	// enters an open nullable function slot, bir2cir can move the generated `invoke` slots with the delegate without
	// rewriting the public signature of the selected member.
	val boundRecv = if (dispatchIdx >= 0 && !hasExt) node.arguments.getOrNull(dispatchIdx) else null
	if (boundRecv != null && ownerClass != null) {
		val regs = regularParams(fn)
		if (regs.size != resolvedFuncType.params.size)
			return unsupported(node, "this bound member function reference",
				"its use-site function arity does not match the resolved declaration")
		val virtual = fn.modality != Modality.FINAL || fn.overriddenSymbols.isNotEmpty()
		val owner = ownerSpec(ownerClass, boundRecv.type)
		// A companion value is compiler representation vocabulary rather than an ordinary capturable CLR value. Use a
		// compiler-owned static adapter whose body retains the semantic companion receiver; bir2cir resolves that receiver
		// and the member association together. The adapter, unlike the authored member, may safely follow an open delegate
		// slot when nullable type-variable erasure changes its physical parameter representation.
		if (ownerClass.isCompanion) {
			val companionOwner = ownerClass.parent as? IrClass
				?: return unsupported(node, "this companion function reference",
					"its companion owner is not a class")
			val adapterReceiver = kotc.bir.BirCompanionValue(TypeNode.Fqn(typeName(companionOwner)),
				TypeNode.Fqn(typeName(ownerClass)), ownerClass.name.asString())
			val lname = freshLiftedMethodName("fnref")
			val freeTps = freeTypeParams(listOf(node.type))
			val adapterTypeArgs = liftedTypeArguments(freeTps)
			val declaration = withLiftedMethodFrame(freeTps) {
				val liftedFuncType = birType(node.type) as TypeNode.Fn
				val parameters = regs.zip(liftedFuncType.params).map { (parameter, type) ->
					BirParameter(parameter.name.asString(), type)
				}
				val arguments = regs.map { BirLocal(it.name.asString()) }
				val liftedOwner = ownerSpec(ownerClass, boundRecv.type)
				val liftedReferenceTypeArgs = functionReferenceTypeArgs(node, fn)
					?: error("validated companion function reference lost its type arguments in its lifted method frame")
				val rawCall = BirCallInstance(ownerType = liftedOwner, virtual = virtual, recv = adapterReceiver,
					method = fn.name.asString(), sig = overloadSignature(fn), typeArgs = liftedReferenceTypeArgs.takeIf { it.isNotEmpty() },
					args = arguments, anySlot = true.takeIf { isAnySlotMethod(fn) }, declarationId = memberDeclarationIdentity)
				val call = memberVisibilityStamped(fn, rawCall)
				val body = if (liftedFuncType.ret == TypeNode.Fqn("kotlin.Unit")) BirExpressionStatement(call) else BirReturn(call)
				liftedFunction(lname, freeTps, parameters, liftedFuncType.ret, listOf(body))
			}
			liftedMethods.add(declaration)
			val delegate = liftedDelegate(lname, resolvedFuncType, adapterTypeArgs)
			// A direct `Companion::member` receiver is already the pure singleton-value fact. Keep the delegate at the
			// expression root so a surrounding open function slot can align its physical signature.
			if (boundRecv is IrGetObjectValue) return delegate
			// Bound-reference creation evaluates its receiver eagerly exactly once. The companion value is a singleton and
			// is not used by the adapter, but an arbitrary expression producing that value can still have observable side
			// effects. Bind the result to a local rather than emitting a value-returning expression statement: ilemit emits
			// expression statements one-to-one and therefore does not synthesize a pop for the unused companion value.
			val evaluatedReceiverName = "__companionReceiver${scopeCounter++}"
			return sourceValueBlock(
				type = resolvedFuncType,
				stmts = listOf(kotc.bir.BirVariable(name = evaluatedReceiverName, type = TypeNode.Fqn("kotlin.Any"), init = kotc.bir.BirField.Value(expr(boundRecv)))),
				result = delegate,
			)
		}
		val selfT = birType(boundRecv.type)
		// State the capture slot explicitly. The receiver expression may carry a broader physical representation than
		// its FIR result type (notably `nullableStruct!!`: the check expression still contains Nullable<V> internally,
		// while the captured receiver is V). bir2cir owns whether that stated write requires Nullable.Value, unboxing,
		// or no conversion; kotc supplies only the semantic source expression and destination type.
		val captureName = "__recvCapture${scopeCounter++}"
		val captureValue = sourceValueBlock(
			type = selfT,
			stmts = listOf(kotc.bir.BirVariable(name = captureName, type = selfT, init = kotc.bir.BirField.Value(expr(boundRecv)))),
			result = kotc.bir.BirLocal(captureName),
		)
		val cname = "dotkt\$${synthScope}\$Closure${closureCounter++}"
		val invokeParams = regs.zip(resolvedFuncType.params).map { (parameter, type) ->
			BirLambdaParameter(parameter.name.asString(), type)
		}
		val arguments = regs.map { BirLocal(it.name.asString()) }
		val recv = kotc.bir.BirFieldGet(ownerType = TypeNode.Fqn(cname), recv = kotc.bir.BirThis(), name = "__recv")
		val declaration = selectedDeclaration ?: if (fn.isFakeOverride) fn.resolveFakeOverride() ?: fn else fn
		val declarationOwner = declaration.parent as? IrClass
		val externalDeclaration = declarationOwner?.let { isExternalNetType(it) } == true
		val signature = overloadSignature(if (externalDeclaration) declaration else fn)
		val callOwner = if (externalDeclaration) {
			val constructed = correspondingSupertypeInstantiation(
				boundRecv.type, declarationOwner!!, allowCapturedArguments = true,
			) ?: error("No selected reference owner for ${declaration.name} on ${boundRecv.type}")
			birType(constructed)
		} else owner
		val resultType = resolvedFuncType.ret
		val overrideTarget = if (externalDeclaration) declaration else fn
		val overrideFacts = inMemberDeclarationFrame(overrideTarget) { overrideDeclarations(overrideTarget) }
		val rawCall = if (!isExternalNetType(ownerClass) || memberDeclarationIdentity != null)
			BirCallInstance(ownerType = callOwner, virtual = virtual, recv = recv, method = fn.name.asString(),
				sig = signature, typeArgs = referenceTypeArgs.takeIf { it.isNotEmpty() }, ret = resultType, sty = resultType,
				args = arguments, overrides = overrideFacts, anySlot = true.takeIf { isAnySlotMethod(fn) },
				declarationId = memberDeclarationIdentity, clrFlagsOperation = flagsOperation)
		else {
			// A projected CLR member without a DotKt declaration identity still needs the adapter. Emit the same neutral
			// external-call facts as an ordinary invocation; bir2cir resolves their physical member representation.
			val argTypes = regs.map { birType(it.type) }
			val physicalName = clrName(ownerClass)
				?: error("validated external bound reference lost its physical owner")
			val physicalOwner = (selfT as? TypeNode.Fqn)?.let { TypeNode.Fqn(physicalName, it.args) }
				?: TypeNode.Fqn(physicalName)
			val selectedOwner = if (externalDeclaration) callOwner else physicalOwner
			BirCallInstance(ownerType = selectedOwner, virtual = virtual, recv = recv, method = fn.name.asString(),
				sig = signature, typeArgs = referenceTypeArgs.takeIf { it.isNotEmpty() }, argTypes = argTypes,
				ret = resultType, sty = resultType, args = arguments, overrides = overrideFacts,
				anySlot = true.takeIf { isAnySlotMethod(fn) }, clrFlagsOperation = flagsOperation)
		}
		val call = memberVisibilityStamped(if (externalDeclaration) declaration else fn, rawCall)
		val body = if (resolvedFuncType.ret == TypeNode.Fqn("kotlin.Unit")) BirExpressionStatement(call) else BirReturn(call)
		val nodeTypeArgs = (node.type as? IrSimpleType)?.arguments
			?.mapNotNull { (it as? IrTypeProjection)?.type }.orEmpty()
		val freeTps = freeTypeParams(listOf(boundRecv.type) + nodeTypeArgs)
		val typeArgs = liftedTypeArguments(freeTps)
		val synthClass = BirClosureDefinition(cname, listOf(BirCaptureDescriptor("__recv", selfT)), invokeParams,
			resolvedFuncType.ret, listOf(body), typeParams = typeParameterDeclarations(freeTps).takeIf { freeTps.isNotEmpty() },
			semanticOwner = activeSemanticOwner ?: fileClass)
		return BirNewClosure(TypeNode.Fqn(cname), listOf(captureValue), "invoke", resolvedFuncType, synthClass, typeArgs = typeArgs)
	}
	// `Class::method` (UNbound) -> a lifted static `__mref(self, args) = self.method(args)`; the receiver
	// becomes the delegate's first parameter. User classes only (`Func<UserType,…>` resolves via DelegateCtor).
	if (dispatchIdx >= 0 && boundRecv == null && !hasExt && ownerClass != null && !isExternalNetType(ownerClass)) {
		val effectiveIdentity = memberDeclarationIdentity
		val ps = fn.parameters.filter { it.kind == IrParameterKind.Regular }
		val lname = freshLiftedMethodName("mref")
		// The forwarder's generic vector belongs to the FIR-resolved callable-reference TYPE. The target declaration's
		// receiver owns a different `T` even when the use site substituted it with an enclosing parameter.
		val freeTps = freeTypeParams(listOf(node.type))
		val typeArgs = liftedTypeArguments(freeTps)
		val declaration = withLiftedMethodFrame(freeTps) {
			val liftedFnType = birType(node.type) as TypeNode.Fn
			val selfT = liftedFnType.params.first()
			val parameters = listOf(BirParameter("__self", selfT)) +
				ps.zip(liftedFnType.params.drop(1)).map { (p, type) -> BirParameter(p.name.asString(), type) }
			val arguments = ps.map { BirLocal(it.name.asString()) }
			val virtual = fn.modality != Modality.FINAL || fn.overriddenSymbols.isNotEmpty()
			val liftedReferenceTypeArgs = functionReferenceTypeArgs(node, fn)
				?: error("validated function reference lost its type arguments while entering its lifted method frame")
			val target = selectedDeclaration ?: if (fn.isFakeOverride) fn.resolveFakeOverride() ?: fn else fn
			val targetOwner = target.parent as? IrClass
			val externalTarget = targetOwner?.let { isExternalNetType(it) } == true
			val selectedOwner = if (externalTarget) {
				val receiverType = ((node.type as IrSimpleType).arguments.first() as IrTypeProjection).type
				val constructed = kotc.frontend.SelectedCallDeclarations.referenceOwnerAt(
					sourcePathOf(node), node.endOffset, fn.name.asString(),
				) ?: correspondingSupertypeInstantiation(
					receiverType, targetOwner!!, allowCapturedArguments = true,
				) ?: error("No selected reference owner for ${target.name} on $receiverType")
				birType(constructed)
			} else selfT
			val signature = overloadSignature(if (externalTarget) target else fn)
			val resultType = liftedFnType.ret
			val overrideTarget = if (externalTarget) target else fn
			val overrideFacts = inMemberDeclarationFrame(overrideTarget) { overrideDeclarations(overrideTarget) }
			val callE = BirCallInstance(ownerType = selectedOwner, virtual = virtual, recv = BirLocal("__self"),
				method = fn.name.asString(), sig = signature, typeArgs = liftedReferenceTypeArgs.takeIf { it.isNotEmpty() },
				ret = resultType, sty = resultType, args = arguments, overrides = overrideFacts,
				anySlot = true.takeIf { isAnySlotMethod(fn) }, declarationId = effectiveIdentity)
			val retT = liftedFnType.ret
			val body = if (fn.returnType.isUnit()) BirExpressionStatement(callE) else BirReturn(callE)
			liftedFunction(lname, freeTps, parameters, retT, listOf(body))
		}
		liftedMethods.add(declaration)
		return liftedDelegate(lname, resolvedFuncType, typeArgs)
	}
	// External unbound members do not enter the Kotlin-owner branch above. Their forwarder retains the
	// frontend-selected constructed owner and signature, just like an ordinary projected instance call.
	if (dispatchIdx >= 0 && boundRecv == null && !hasExt && ownerClass != null && clrName(ownerClass) != null) {
		val regs = regularParams(fn)
		val lname = freshLiftedMethodName("mref")
		val freeTps = freeTypeParams(listOf(node.type))
		val typeArgs = liftedTypeArguments(freeTps)
		val declaration = withLiftedMethodFrame(freeTps) {
			val liftedFuncType = birType(node.type) as TypeNode.Fn
			val selfT = liftedFuncType.params.first()
			val target = selectedDeclaration ?: if (fn.isFakeOverride) fn.resolveFakeOverride() ?: fn else fn
			val targetOwner = target.parent as IrClass
			val receiverType = ((node.type as IrSimpleType).arguments.first() as IrTypeProjection).type
			val selectedOwner = (kotc.frontend.SelectedCallDeclarations.referenceOwnerAt(
				sourcePathOf(node), node.endOffset, fn.name.asString(),
			) ?: correspondingSupertypeInstantiation(receiverType, targetOwner, allowCapturedArguments = true))
				?.let { birType(it) } ?: error("No selected reference owner for ${target.name} on $receiverType")
			val parameters = listOf(BirParameter("__self", selfT)) +
				regs.zip(liftedFuncType.params.drop(1)).map { (parameter, type) -> BirParameter(parameter.name.asString(), type) }
			val arguments = regs.map { BirLocal(it.name.asString()) }
			val liftedReferenceTypeArgs = functionReferenceTypeArgs(node, fn)
				?: error("validated .NET method reference lost its type arguments in its lifted method frame")
			val call = BirCallInstance(ownerType = selectedOwner, method = fn.name.asString(), sig = overloadSignature(target),
				typeArgs = liftedReferenceTypeArgs.takeIf { it.isNotEmpty() }, argTypes = regs.map { birType(it.type) },
				ret = liftedFuncType.ret, recv = BirLocal("__self"), args = arguments,
				anySlot = true.takeIf { isAnySlotMethod(fn) }, clrFlagsOperation = flagsOperation,
				declarationId = memberDeclarationIdentity.takeUnless { selfT.containsStarProjection() })
			val body = if (fn.returnType.isUnit()) BirExpressionStatement(call) else BirReturn(call)
			liftedFunction(lname, freeTps, parameters, liftedFuncType.ret, listOf(body))
		}
		liftedMethods.add(declaration)
		return liftedDelegate(lname, resolvedFuncType, typeArgs)
	}
	return unsupported(node, "a method reference to a .NET method (`::${fn.name}`)",
		"wrap the call in a lambda instead, e.g. `{ a -> x.${fn.name}(a) }`")
}

/** Method type arguments selected by FIR for a callable reference. An empty list means a non-generic target;
 * null means a generic definition was not closed and must never reach a delegate target or forwarding call. */
internal fun BirEmitter.functionReferenceTypeArgs(
	node: IrFunctionReference,
	fn: IrSimpleFunction,
): List<TypeNode>? {
	if (fn.typeParameters.isEmpty()) return emptyList()
	val args = fn.typeParameters.indices.map { node.typeArguments.getOrNull(it) }
	if (args.any { it == null }) return null
	return args.map { birType(it!!) }
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
internal fun BirEmitter.suspendFunctionRef(node: IrFunctionReference, fn: IrSimpleFunction, dispatchIdx: Int): BirExpression {
	val fnType = birType(node.type) as? TypeNode.Fn
		?: return unsupported(node, "a suspend function reference",
			"its inferred type was not a resolvable suspend function type")
	val referenceTypeArgs = functionReferenceTypeArgs(node, fn)
		?: return unsupported(node, "a generic suspend function reference",
			"its FIR-resolved call-site type arguments are incomplete")
	val boundRecv = if (dispatchIdx >= 0) node.arguments.getOrNull(dispatchIdx) else null
	// Lazy referenced declarations may retain a phantom ExtensionReceiver parameter after companion-static
	// normalization. Use the same semantic receiver helper as ordinary calls, so callable-reference arity follows
	// the declaration Kotlin actually exposes rather than that stale physical slot.
	val extParam = extensionReceiverParam(fn)
	val extIdx = if (extParam == null) -1 else fn.parameters.indexOf(extParam)
	val boundExt = if (extIdx >= 0) node.arguments.getOrNull(extIdx) else null
	val ownerClass = fn.parent as? IrClass
	val identityDeclaration = (node.reflectionTarget?.owner as? IrSimpleFunction) ?: fn
	val selectedDeclarationId = declarationIdForPhysicalAllocation(identityDeclaration)
	val physicalIdentityDeclaration = if (identityDeclaration.isFakeOverride && selectedDeclarationId == null)
		identityDeclaration.resolveFakeOverride() ?: identityDeclaration else identityDeclaration
	val declarationIdentity = selectedDeclarationId
		?: declarationIdForPhysicalAllocation(physicalIdentityDeclaration) ?: declarationIdForPhysicalAllocation(fn)
	val companionClass =
		(boundRecv as? IrGetObjectValue)?.symbol?.owner?.takeIf { it.isCompanion }
			?: ownerClass?.takeIf { it.isCompanion }
	val semanticCompanionType = (boundRecv?.let { birType(it.type) } as? TypeNode.Fqn)
		?.takeIf { ".<companion:" in it.name }
	val member = fn.name.asString()
	val companionReceiver = companionReceiverCallType(fn, node)
	val ret = fnType.ret
	val virtual = fn.modality != Modality.FINAL || fn.overriddenSymbols.isNotEmpty()
	val anySlot = true.takeIf { isAnySlotMethod(fn) }
	val local = localFns[fn]
	val localFrame = local?.let { localFunctionReferenceFrame(node, fn, it) }
	// The lambda's OWN physical params = its extension receiver (when the callable type has one), then ordinary
	// params. TypeNode.Fn deliberately keeps a Kotlin extension receiver in `recv` rather than duplicating it in
	// `params`; newSuspendLambda's invoke/create protocol still receives it as physical arg0, exactly like source
	// receiver lambdas (`suspend T.() -> R`).
	val physicalParamTypes = listOfNotNull(fnType.recv) + fnType.params
	val paramNames = physicalParamTypes.indices.map { "__p$it" }
	val parameters = physicalParamTypes.mapIndexed { i, t -> BirLambdaParameter(paramNames[i], t) }
	fun localArg(name: String) = BirLocal(name)
	// Build (captures, capValues, bodyCall) by reference kind. The inner call MIRRORS the direct-call shape for this
	// callee so bir2cir attributes it identically; `suspendCall:true` is the fact SuspendColdLowering's FunGen segments on.
	val captures: List<BirCaptureDescriptor>; val capValues: List<BirExpression>?; val bodyCall: BirExpression
	when {
		local != null -> {
			val ownParams = localFunctionOwnParams(fn)
			val bound = localFunctionBoundArguments(node, fn)
			val boundNames = bound.indices.map { "dotkt\$bound$it" }
			val boundFields = bound.zip(boundNames).associate { (binding, name) -> binding.first to name }
			val remaining = ownParams.filter { it !in boundFields }
			check(remaining.size == paramNames.size) { "local suspend reference lost its frontend argument binding" }
			val arguments = remaining.zip(paramNames).toMap()
			val capPairs = uniqueCaptureNames(
				local.captures, (paramNames + boundNames).toMutableSet(), alwaysPrefix = true)
			val capDescriptors = capPairs.map { (declaration, fieldName) ->
				BirCaptureDescriptor(fieldName, captureFieldType(declaration))
			}
			val boundDescriptors = bound.zip(boundNames).map { (binding, name) ->
				BirCaptureDescriptor(name, birType(localFrame!!.parameterTypes.getValue(binding.first)))
			}
			captures = capDescriptors + boundDescriptors
			capValues = (local.captures.map { capValueExpr(it) } + bound.map { expr(it.second) })
				.takeIf { it.isNotEmpty() }
			val args = (capPairs.map { (_, fieldName) -> localArg(fieldName) } + ownParams.map { parameter ->
				localArg(boundFields[parameter] ?: arguments.getValue(parameter))
			})
			val sig = capPairs.map { (declaration, _) -> captureFieldType(declaration) } + ownParams.map { birType(it.type) }
			val localTypeArgs = if (local.typeParams.isEmpty()) null else localFunctionTypeArgs(local, fn, node.typeArguments)
			bodyCall = BirCallLocal(local.id, args, sig = sig, typeArgs = localTypeArgs, suspendCall = true)
		}
		extIdx >= 0 && dispatchIdx >= 0 ->
			return unsupported(node, "a suspend member extension-function reference",
				"a member extension function cannot be referenced as a callable in Kotlin")
		extIdx >= 0 && boundExt != null -> {
			// Bound top-level extension `value::ext`: capture the extension receiver exactly once, just as the
			// dispatch receiver of `value::member`; the lambda's own params are the target's regular args.
			captures = listOf(BirCaptureDescriptor("__recv", birType(boundExt.type)))
			capValues = listOf(expr(boundExt))
			val args = listOf(localArg("__recv")) + paramNames.map(::localArg)
			bodyCall = extensionReferenceCall(node, fn, ret, args, suspending = true)
		}
		extIdx >= 0 -> {
			// Unbound top-level extension `Type::ext`: the extension receiver is the leading lambda parameter.
			if (paramNames.isEmpty())
				return unsupported(node, "a suspend extension-function reference",
					"its inferred type carries no extension-receiver parameter")
			captures = emptyList(); capValues = null
			val args = paramNames.map(::localArg)
			bodyCall = extensionReferenceCall(node, fn, ret, args, suspending = true)
		}
		dispatchIdx >= 0 && boundRecv != null && ownerClass != null -> {
			// Bound member `obj::m` — receiver captured into `__recv`; all lambda params are the target's regular args.
			val args = paramNames.map(::localArg)
			captures = listOf(BirCaptureDescriptor("__recv", birType(boundRecv.type)))
			capValues = listOf(expr(boundRecv))
			val semanticOwner = companionClass ?: ownerClass
			val externalCompanionOwner = companionClass?.let { clrExternalOwner(it) }
			val receiverType = birType(boundRecv.type)
			val trustedStarOwner = receiverType.containsStarProjection() && declarationIdentity != null
			val owner = externalCompanionOwner?.let { TypeNode.Fqn(it) }
				?: semanticCompanionType ?: if (trustedStarOwner) receiverType else ownerSpec(semanticOwner, boundRecv.type)
			bodyCall = BirCallInstance(ownerType = owner, virtual = virtual, recv = localArg("__recv"), method = member,
				sig = overloadSignature(fn), typeArgs = referenceTypeArgs.takeIf { it.isNotEmpty() }, args = args, anySlot = anySlot,
				suspendCall = true, companionCall = true.takeIf { externalCompanionOwner != null }, declarationId = declarationIdentity)
		}
		dispatchIdx >= 0 && boundRecv == null && ownerClass != null -> {
			// Unbound member `Type::m` — the leading lambda param is the receiver, the rest are the target's regular args.
			if (paramNames.isEmpty())
				return unsupported(node, "a suspend function reference",
					"its inferred type carries no dispatch-receiver parameter")
			val args = paramNames.drop(1).map(::localArg)
			captures = emptyList(); capValues = null
			bodyCall = BirCallInstance(ownerType = ownerSpec(ownerClass, fn.parameters[dispatchIdx].type), virtual = virtual,
				recv = localArg(paramNames.first()), method = member, sig = overloadSignature(fn),
				typeArgs = referenceTypeArgs.takeIf { it.isNotEmpty() }, args = args, anySlot = anySlot, suspendCall = true, declarationId = declarationIdentity)
		}
		dispatchIdx < 0 && extIdx < 0 && ownerClass != null &&
			(isExternalNetType(ownerClass) || isKotlinStaticFunction(fn)) -> {
			// Kotlin 2.4 companion-block suspend members have no dispatch receiver, but they are not top-level
			// declarations: their declaring class remains the semantic owner that bir2cir must realize physically.
			// Keep the same owner-bearing call shape as a direct static invocation so the suspend adapter cannot
			// degrade to an ownerless file-facade call (whose lazy declaration has no calleeOwner in this module).
			val staticOwner = (if (isExternalNetType(ownerClass)) clrName(ownerClass) else typeName(ownerClass))
				?: return unsupported(node, "a suspend static function reference",
					"its declaring owner identity is missing")
			val args = paramNames.map(::localArg)
			captures = emptyList(); capValues = null
			bodyCall = BirCallStatic(ownerType = TypeNode.Fqn(staticOwner), method = member, sig = overloadSignature(fn),
				typeArgs = referenceTypeArgs.takeIf { it.isNotEmpty() }, args = args, suspendCall = true, declarationId = declarationIdentity)
		}
		dispatchIdx < 0 -> {
			// Top-level `::fn` — all lambda params are the target's regular args; an owner:null static call.
			val args = paramNames.map(::localArg)
			captures = emptyList(); capValues = null
			bodyCall = BirCallStatic(owner = BirField.Null, method = member, sig = overloadSignature(fn),
				typeArgs = referenceTypeArgs.takeIf { it.isNotEmpty() }, args = args, suspendCall = true,
				calleeOwner = calleeOwnerType(fn), companionReceiver = companionReceiver, declarationId = declarationIdentity)
		}
		else -> return unsupported(node, "a suspend function reference",
			"its receiver could not be resolved to a supported (top-level or user-member) suspend target")
	}
	val body = if (ret == TypeNode.Fqn("kotlin.Unit")) BirExpressionStatement(bodyCall) else BirReturn(bodyCall)
	// The SM must be generic over any enclosing type params in the reference's signature (reified CLR generics); bare
	// names (bir2cir prepends `gp:`), consistent with suspendLambda().
	val freeTps = (localFrame?.typeParams
		?: freeTypeParams(listOf(node.type) + listOfNotNull(boundRecv?.type, boundExt?.type)))
		.sortedWith(compareBy<IrTypeParameter>(
			{ if (tvOf(it).scope == "type") 0 else 1 },
			{ tvOf(it).i },
		))
	val typeParameters = typeParameterDeclarations(freeTps)
	val typeArgs = liftedTypeArguments(freeTps)
	val externalCompanionOwner = companionClass?.let { clrExternalOwner(it) }
	val localCompanionCaptureOwner = semanticCompanionType?.name
		?: companionClass?.takeIf { clrExternalOwner(it) == null }?.let { typeName(it) }
	return BirNewSuspendLambda(physicalParamTypes.size, captures, parameters, ret, typeParameters, listOf(body), fnType,
		capValues = capValues, typeArgs = typeArgs, externalCompanionOwner = externalCompanionOwner, companionCaptureOwner = localCompanionCaptureOwner)
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
	callArgs: List<BirExpression>,
	suspending: Boolean = false,
): BirCallStatic {
	val refTps = fn.typeParameters
	val refTaArgs = refTps.indices.map { node.typeArguments.getOrNull(it) }
	val hasRefTa = refTps.isNotEmpty() && refTaArgs.all { it != null }
	val refTa = if (!hasRefTa) null else refTaArgs.map { birType(it!!) }
	val suspendCall = true.takeIf { suspending }
	// A companion-extension association and the selected accessor/function identity are orthogonal. The former
	// chooses the CLR container; the latter prevents overload re-resolution after receiver/parameter erasure.
	val declarationIdentity = declarationIdForPhysicalAllocation(fn)
	val externalFileClass = clrExternalOwner(fn)
	if (externalFileClass != null) {
		val extRecvParam = extensionReceiverParam(fn)!!
		val declShapeTypes = (listOf(extRecvParam) + regularParams(fn))
			.map { birType(it.type) }
		return if (hasRefTa)
			BirCallStatic(ownerType = TypeNode.Fqn(externalFileClass), method = fn.name.asString(), typeArgs = refTa,
				shapeTypes = declShapeTypes, args = callArgs, suspendCall = suspendCall, declarationId = declarationIdentity)
		else
			BirCallStatic(ownerType = TypeNode.Fqn(externalFileClass), method = fn.name.asString(), argTypes = declShapeTypes,
				ret = ret, args = callArgs, suspendCall = suspendCall, declarationId = declarationIdentity)
	}
	return BirCallStatic(owner = BirField.Null, method = fn.name.asString(), sig = overloadSignature(fn),
		typeArgs = refTa, ret = ret.takeIf { hasRefTa }, args = callArgs, suspendCall = suspendCall,
		calleeOwner = calleeOwnerType(fn), declarationId = declarationIdentity)
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
internal fun BirEmitter.boundExtFnRef(node: IrFunctionReference, fn: IrSimpleFunction, boundExt: IrExpression): BirExpression {
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
	val invokeParams = regTypes.mapIndexed { i, t -> BirLambdaParameter("__a${i + 1}", t) }
	val recvArg = kotc.bir.BirFieldGet(ownerType = TypeNode.Fqn(cname), recv = kotc.bir.BirThis(), name = "__recv")
	val callArgs = listOf(recvArg) + regTypes.indices.map { BirLocal("__a${it + 1}") }
	val callE = extensionReferenceCall(node, fn, retT, callArgs)
	val forwardBody = if (retVoid) BirExpressionStatement(callE) else BirReturn(callE)
	// The closure must be GENERIC over any enclosing type params referenced by the captured receiver or the invoke
	// signature (reified CLR generics) — mirrors `lambda()`'s freeTps over capture+param+ret types.
	val nodeTypeArgs = (node.type as? IrSimpleType)?.arguments?.mapNotNull { (it as? IrTypeProjection)?.type }.orEmpty()
	val freeTps = freeTypeParams(listOf(boundExt.type) + nodeTypeArgs)
	val typeArgs = liftedTypeArguments(freeTps)
	val synthClass = BirClosureDefinition(cname, listOf(BirCaptureDescriptor("__recv", selfT)), invokeParams, retT, listOf(forwardBody),
		typeParams = typeParameterDeclarations(freeTps).takeIf { freeTps.isNotEmpty() }, semanticOwner = activeSemanticOwner ?: fileClass)
	return BirNewClosure(TypeNode.Fqn(cname), listOf(expr(boundExt)), "invoke", fnType, synthClass, typeArgs = typeArgs)
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
internal fun BirEmitter.adapterRef(node: IrFunctionReference, fn: IrSimpleFunction): BirExpression {
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
	val invokeParams = invokeDecls.map { p ->
		val ty = if (p.type.isUnit()) TypeNode.Fqn("kotlin.Unit") else birTypeDeleg(p.type)
		BirLambdaParameter(p.name.asString(), ty)
	}
	// Keep the synthetic vector deterministic in the source frame (type params before method params, then position).
	// `liftedTypeArguments` separately applies the CURRENT lexical substitution, and ClosureSynthesis uses that explicit
	// correspondence to rebind every payload occurrence into the synthesized class's dense type-parameter frame.
	val freeTps = freeTypeParams(boundCaps.map { it.first.type } + invokeDecls.map { it.type } + listOf(fn.returnType) + bodyTypeOperands(fn))
		.sortedWith(compareBy({ if (tvOf(it).scope == "type") 0 else 1 }, { tvOf(it).i }))
	val typeArgs = liftedTypeArguments(freeTps)
	// No captured receiver (`Type::member` fully unbound) -> a lifted static delegate; the leading receiver param(s) ride
	// as the forwarder's own params (referenced by name in the body).
	if (boundCaps.isEmpty()) {
		val lname = freshLiftedMethodName("mref")
		val declaration = withLiftedMethodFrame(freeTps) {
			val liftedFnType = (birType(node.type) as? TypeNode.Fn) ?: funcTypeOf(fn)
			val liftedParams = invokeDecls.zip(liftedFnType.params).map { (parameter, type) ->
				BirParameter(parameter.name.asString(), type)
			}
			val body = withFreshLambdaLocalFunctionIds(fn) {
				(fn.body as? IrBlockBody)?.statements.orEmpty().flatMap { stmt(it) }
			}
			liftedFunction(lname, freeTps, liftedParams, liftedFnType.ret, body)
		}
		liftedMethods.add(declaration)
		return liftedDelegate(lname, fnType, typeArgs)
	}
	// Bound receiver(s) -> a capture-class closure (mirrors `lambda()`'s capturing branch); the bound value(s) are the
	// capture exprs, and the adapter body's receiver-param reads rewrite to the capture fields via `captureSubst`.
	val cname = "dotkt\$${synthScope}\$Closure${closureCounter++}"
	val capPairs = boundCaps.map { (p, _) -> p to captureFieldName(p) }
	val savedSubst = capPairs.associate { (decl, _) -> decl to captureSubst[decl] }
	capPairs.forEach { (decl, fname) ->
		captureSubst[decl] = kotc.bir.BirFieldGet(ownerType = TypeNode.Fqn(cname), recv = kotc.bir.BirThis(), name = fname)
	}
	val body = withFreshLambdaLocalFunctionIds(fn) {
		(fn.body as? IrBlockBody)?.statements.orEmpty().flatMap { stmt(it) }
	}
	capPairs.forEach { (decl, _) -> val prev = savedSubst[decl]; if (prev != null) captureSubst[decl] = prev else captureSubst.remove(decl) }
	val fields = capPairs.map { (decl, fname) -> BirCaptureDescriptor(fname, captureFieldType(decl)) }
	val capExprs = boundCaps.map { (_, valueExpr) -> expr(valueExpr) }
	val synthClass = BirClosureDefinition(cname, fields, invokeParams, ret, body,
		typeParams = typeParameterDeclarations(freeTps).takeIf { freeTps.isNotEmpty() }, semanticOwner = activeSemanticOwner ?: fileClass)
	return BirNewClosure(TypeNode.Fqn(cname), capExprs, "invoke", fnType, synthClass, typeArgs = typeArgs)
}

/**
 * The materialization of a delegate accessor's compiler-synthesized `KProperty` argument (a `getValue`/
 * `setValue`/`provideDelegate` 2nd arg, IR origin `PROPERTY_REFERENCE_FOR_DELEGATE`) -> `new
 * kotlin.reflect.ClrPropertyStub(name)`, the REAL emitted stdlib name-only impl of `KProperty<Any?>`
 * (KCallable.name + KAnnotatedElement.annotations). Kotlin's own delegate convention only ever reads `.name`
 * off this argument in ordinary bodies — never get()/set()/invoke() — so this cheap stub (not the full
 * `propertyRef` lift below) is always correct for it. Replaces the retired `dotkt$KPropertyImpl` synthetic.
 */
internal fun BirEmitter.kPropertyStub(name: String): BirNew =
	BirNew(type = TypeNode.Fqn("kotlin.reflect.ClrPropertyStub", listOf(OBJ)), argTypes = listOf(TypeNode.Fqn("kotlin.String")),
		args = listOf(BirConstant(type = TypeNode.Fqn("kotlin.String"), value = BirLiteral.TextLiteral(name))))

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
 * property with both dispatch and extension receivers (`KProperty2`) is a clean deferral. `@ClrField` member
 * references use the backing-field path below. The compiler-synthesized KProperty argument of a
 * delegate's getValue/setValue/provideDelegate is NOT this path — those call sites materialize `kPropertyStub` directly
 * without going through `expr()`/this dispatch; the origin check below is a defensive fallback only.
 */
internal fun BirEmitter.propertyRef(node: IrPropertyReference): BirExpression {
	if (node.origin == IrStatementOrigin.PROPERTY_REFERENCE_FOR_DELEGATE)
		return kPropertyStub(node.symbol.owner.name.asString())
	// A TOP-LEVEL extension property reference (`obj::extProp` bound, or `Type::extProp` unbound) IS supported: it
	// lowers below through a static `<name>` accessor call carrying the ext receiver as the leading arg, MIRRORING
	// the top-level ext-property value-read path in `call()` (#21). Only a genuine KProperty2 — a MEMBER extension
	// property with BOTH a dispatch AND an extension receiver, inexpressible as a plain callable in Kotlin — stays
	// unsupported. Test the accessor's PARAMETER SHAPE, not the bound ARGUMENT (a bound test is null for an UNBOUND
	// ref, so it would misclassify an unbound top-level ext ref).
	val extAccessor = node.getter?.owner ?: node.setter?.owner
	// `extensionReceiverParam`, not a raw parameter-kind scan: a COMPANION EXTENSION has no receiver parameter,
	// and a cross-module one only appears to declare one because fir2ir's lazy declaration builder adds it back
	// (see [isCompanionExtensionCallee]). Reading the raw kind here sent `C::bar` for a `companion val C.bar`
	// down the top-level-extension path, whose accessor call then dereferenced that absent parameter.
	val hasExtRecv = extAccessor?.let { extensionReceiverParam(it) } != null
	val hasDispatchRecv = extAccessor?.parameters?.any { it.kind == IrParameterKind.DispatchReceiver } == true
	if (hasExtRecv && hasDispatchRecv)
		return unsupported(node, "this property reference",
			"a member extension-receiver property reference (KProperty2) has no supported lowering yet")
	val prop = node.symbol.owner
	val companionReceiver = kotc.frontend.ClrCompanionExtensions.receiverTypeAtUse(sourcePathOf(node), node.startOffset, node.endOffset)
	val companionExtension = isCompanionExtensionCallee(prop) || companionReceiver != null
	// A `const val` / `@ClrField` member property has PLAIN-BACKING-FIELD storage (no get_/set_
	// accessor slot) —
	// its get/set below read/write the field directly (`field`/`setFieldExpr`), the SAME nodes the
	// ordinary member-property access path emits for these (BirEmitterCalls). `fieldBacked` gates that in readBody/
	// setMethod; everything else (interface identity, receiver capture, generics) is shared with the accessor path.
	val getterFn = node.getter?.owner ?: prop.getter
		?: return unsupported(node, "this property reference", "the referenced property has no getter")
	val fieldBacked = prop.isConst || isClrField(prop)
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
	// A property whose Kotlin declaration has static storage needs no captured singleton receiver. In particular,
	// `object O { const val X = ... }; O::X` is a KProperty0 over a CLR static Literal field even though FIR supplies
	// the object value as a dispatch receiver. Preserve that storage fact here instead of turning the reference into
	// an instance-field access to the literal. This is still Kotlin-level classification; bir2cir owns the carrier
	// and final CLR field representation.
	val storageClass = prop.parent as? IrClass
	val hasStaticStorage = storageClass != null && !hasExtRecv && hasStaticPropertyStorage(storageClass, prop)
	// Kotlin 2.4's wrapper getter for an object `const val` can omit its DispatchReceiver parameter even though the
	// property reference retains the authored receiver expression in a sparse argument slot. Parameter-kind lookup
	// therefore cannot recover it. For an object declaration, a single supplied argument is precisely that bound
	// object receiver; keep it solely for eager evaluation, never as the CLR field receiver.
	val staticReceiverToEvaluate = if (!hasStaticStorage) null else
		boundRecv ?: if (storageClass?.kind == ClassKind.OBJECT) node.arguments.filterNotNull().singleOrNull() else null
	// The receiver captured into the lift's `__recv` field for a BOUND reference: the dispatch receiver (a member
	// property) or the extension receiver (a top-level ext property). Only one is ever present. Static storage does not
	// need a captured value, but Kotlin still evaluates an authored bound receiver exactly once at reference creation;
	// that evaluation is preserved in the valueBlock returned below.
	val capturedRecv = if (hasStaticStorage) null else boundRecv ?: extBoundRecv
	val freeTps = freeTypeParams(listOf(node.type) + listOfNotNull(capturedRecv?.type))
	// The construction lives in the enclosing method/type frame, while the lifted property's declaration owns a
	// fresh type-parameter frame. Preserve the former before rebinding every definition-side rendering to `type#i`.
	// ilemit must never guess that a `method#i` embedded in a generated type really meant its `type#i`.
	val constructionTypeArgs = freeTps.map { typeArgSubst[it] ?: tvOf(it) }
	val constructionPropertyInterface = birType(node.type) as? TypeNode.Fqn
		?: return unsupported(node, "this property reference",
			"its inferred type was not a KProperty/KMutableProperty interface")
	val constructionRecvType = if (capturedRecv != null) birType(capturedRecv.type) else null
	val constructionArgs = if (capturedRecv != null) listOf(expr(capturedRecv)) else emptyList()
	val staticReceiverEvaluation = staticReceiverToEvaluate?.let { expr(it) }
	val savedTypeSubst = freeTps.associateWith { typeArgSubst[it] }
	freeTps.forEachIndexed { index, parameter -> typeArgSubst[parameter] = TypeNode.Tv("type", index) }
	return try {
	// This is a semantic companion receiver capture, not an arbitrary object capture. bir2cir consumes the marker
	// when it chooses the companion's physical representation.
	val companionCaptureClass = if (capturedRecv == null) null else
		(boundRecv as? IrGetObjectValue)?.symbol?.owner?.takeIf { it.isCompanion }
			?: declClass?.takeIf { it.isCompanion }
	val semanticCompanionType = (capturedRecv?.let { birType(it.type) } as? TypeNode.Fqn)
		?.takeIf { ".<companion:" in it.name }
	val companionCaptureOwner = semanticCompanionType?.name
		?: companionCaptureClass?.let { clrExternalOwner(it) ?: typeName(it) }

	val ifaceSpec = birType(node.type) as TypeNode.Fqn
	val ifaceArgs = ifaceSpec.args.orEmpty()
	val propertyInterface = node.type.classifierOrNull?.owner as IrClass
	// Synthesized implementations must carry the same resolved override facts as authored methods.
	fun propertySlotOverrides(member: String): List<BirOverride> {
		val slot = propertyInterface.declarations.filterIsInstance<IrSimpleFunction>()
			.single { it.name.asString() == member }
		val declarations = LinkedHashSet<IrSimpleFunction>()
		fun collect(declaration: IrSimpleFunction) {
			if (declarations.add(declaration)) declaration.overriddenSymbols.forEach { collect(it.owner) }
		}
		collect(slot)
		val edges = declarations.map { declaration ->
			val owner = declaration.parent as IrClass
			val ownerType = if (owner === propertyInterface) ifaceSpec else
				birType(correspondingSupertypeInstantiation(node.type, owner)
					?: error("property reference slot '$member' has no resolved supertype instantiation"))
			BirOverride(ownerType, declaration.name.asString(), "method", emittedParamCount(declaration),
				declarationId = declarationIdForPhysicalAllocation(declaration))
		}
		return edges
	}
	val arity0 = ifaceSpec.name == "kotlin.reflect.KProperty0" || ifaceSpec.name == "kotlin.reflect.KMutableProperty0"
	val vType = ifaceArgs.lastOrNull() ?: OBJ
	val recvTypeNode = ifaceArgs.getOrNull(0).takeIf { !arity0 }   // KProperty1/KMutableProperty1's T (unbound only)
	// A STATIC property reference — `C::v` for a Kotlin 2.4 `companion { val v }`, or a direct static declaration from
	// a CLR reference KLIB — is a KProperty0/KMutableProperty0 with NOTHING captured: there is no receiver to bind,
	// and Kotlin 2.4's fake accessor wrapper declares a synthetic dispatch parameter the reference does not fill.
	// Classify from the resolved callable-reference type plus the declaration's own static fact, not that wrapper
	// parameter. A real companion-OBJECT instance member reference captures its singleton and stays on the bound
	// path below; its `const val` is the deliberate exception because that declaration has static storage.
	val staticProperty = arity0 && capturedRecv == null && declClass != null && !hasExtRecv &&
		(isExternalNetType(declClass) || hasStaticStorage)
	val staticPropertyOwner = when {
		!staticProperty -> null
		isExternalNetType(declClass!!) -> clrName(declClass)
		else -> typeName(storageClass ?: declClass!!)
	}
	if (staticProperty && staticPropertyOwner == null)
		return unsupported(node, "this static property reference", "its declaring owner identity is missing")

	// BOUND (KProperty0, receiver captured in `__recv`) vs UNBOUND (KProperty1, receiver = the get/set's leading
	// param), for BOTH a member property (`declClass != null`) and a top-level extension property (`hasExtRecv`).
	// A top-level NON-ext property (`::x`) is neither — it stays the plain static-field/get_ path below.
	val bound = arity0 && capturedRecv != null && (declClass != null || hasExtRecv)
	val unbound = !arity0 && (declClass != null || hasExtRecv)
	val cname = "dotkt\$${synthScope}\$PropRef${closureCounter++}"

	fun recvExprIn(): BirExpression = when {
		bound -> kotc.bir.BirFieldGet(ownerType = TypeNode.Fqn(cname), recv = kotc.bir.BirThis(), name = "__recv")
		unbound -> kotc.bir.BirLocal(name = "receiver")
		else -> BirThis()
	}
	val memberOwner: TypeNode = when {
		staticProperty -> kotc.frontend.ClrStaticOwners.at(sourcePathOf(node), node.endOffset, name, "get")
			?.let(::birType) ?: TypeNode.Fqn(staticPropertyOwner!!)
		fieldBacked && !hasExtRecv && (bound || unbound) -> {
			val receiverType = if (bound) boundRecv!!.type else
				((node.type as? IrSimpleType)?.arguments?.firstOrNull() as? IrTypeProjection)?.type
					?: error("field property reference '$name' has no receiver type")
			fieldDeclarationOwner(getterFn, receiverType)
		}
		bound && semanticCompanionType != null -> semanticCompanionType
		bound && companionCaptureClass != null -> ownerSpec(companionCaptureClass, boundRecv!!.type)
		bound && declClass != null -> ownerSpec(declClass, boundRecv!!.type)
		unbound && declClass != null -> kotc.frontend.SelectedCallDeclarations.referenceOwnerAt(
			sourcePathOf(node), node.endOffset, name,
		)?.let(::birType) ?: recvTypeNode ?: OBJ
		else -> OBJ
	}
	// A top-level EXTENSION property: its getter/setter is a static `<name>` accessor (a `prop:get`/`prop:set` marker
	// carrying the ext receiver as its leading arg), NOT an instance `get_/set_` slot. Mirror the direct value-read
	// path: local/stdlib accessors use `owner:null`; a restored DotKt accessor carries its metadata-derived file class.
	// In both cases kotc projects the resolved Kotlin accessor identity and bir2cir chooses the CLR accessor shape.
	fun extAccessorCall(isSetter: Boolean, valueArg: BirExpression?): BirCallStatic {
		val kind = if (isSetter) "set" else "get"
		val recvArg = if (bound)
			kotc.bir.BirFieldGet(ownerType = TypeNode.Fqn(cname), recv = kotc.bir.BirThis(), name = "__recv")
		else kotc.bir.BirLocal(name = "receiver")
		val args = listOfNotNull(recvArg, valueArg)
		// The `sig` overload key MUST match the accessor being called: the setter's param list is [recv, value], the
		// getter's is [recv]. Using the getter's sig for `prop:set` would key a same-name `var`-ext-property overload
		// to the wrong slot.
		val sigFn = if (isSetter) setterFn!! else getterFn
		val declarationIdentity = declarationIdForPhysicalAllocation(sigFn)
		// A generic extension property reference carries the property accessor's OWN resolved type arguments on the
		// reference node (`List<Int>::lastIndex` -> T=Int). Thread them onto the forwarded accessor call exactly as the
		// direct value-read path does, so bir2cir/ilemit close the generic method rather than seeing an open `!!T`.
		val refTps = sigFn.typeParameters
		val refTaArgs = refTps.indices.map { node.typeArguments.getOrNull(it) }
		val hasRefTa = refTps.isNotEmpty() && refTaArgs.all { it != null }
		val refTa = if (!hasRefTa) null else refTaArgs.map { birType(it!!) }
		val retT = if (isSetter) TypeNode.Fqn("kotlin.Unit") else vType
		if (externalExtPropFileClass != null) {
			val extRecvParam = extensionReceiverParam(sigFn)!!
			val declShapeTypes = (listOf(extRecvParam) + regularParams(sigFn))
				.map { birType(it.type) }
			return if (hasRefTa)
				BirCallStatic(ownerType = TypeNode.Fqn(externalExtPropFileClass), method = name, prop = kind, typeArgs = refTa,
					shapeTypes = declShapeTypes, args = args, declarationId = declarationIdentity)
			else
				BirCallStatic(ownerType = TypeNode.Fqn(externalExtPropFileClass), method = name, prop = kind, argTypes = declShapeTypes,
					ret = retT, args = args, declarationId = declarationIdentity)
		}
		return BirCallStatic(owner = BirField.Null, method = name, prop = kind, sig = overloadSignature(sigFn),
			typeArgs = refTa, ret = retT.takeIf { hasRefTa }, args = args, calleeOwner = calleeOwnerType(sigFn), declarationId = declarationIdentity)
	}
	fun accessorCall(isSetter: Boolean, extraArg: BirExpression?): BirCallInstance {
		val fn = if (isSetter) setterFn!! else getterFn
		val virtual = fn.modality != Modality.FINAL || fn.overriddenSymbols.isNotEmpty()
		val args = listOfNotNull(extraArg)
		val kind = if (isSetter) "set" else "get"
		// A lifted access must retain the same selected declaration and instantiated override edges as a direct
		// access. The fake override's dispatch owner alone does not identify the declaration's storage or slot.
		val declarationIdentity = declarationIdForPhysicalAllocation(fn)
			?: if (fn.isFakeOverride) fn.resolveFakeOverride()?.let(::declarationIdForPhysicalAllocation) else null
		val inherited = if (fn.isFakeOverride) overrideDeclarations(fn) else null
		val externalCompanion = true.takeIf { companionCaptureClass?.let { clrExternalOwner(it) != null } == true }
		return BirCallInstance(ownerType = memberOwner, virtual = virtual, recv = recvExprIn(), method = name, prop = kind,
			sig = overloadSignature(fn), args = args, companionCall = externalCompanion, declarationId = declarationIdentity, overrides = inherited)
	}
	// A field-backed member property (`@ClrField`) reads/writes its backing field directly — the SAME
	// `field`/`setFieldExpr` shapes the ordinary member-access path emits — over the lift's receiver
	// (bound = the captured `__recv` field, unbound = the `receiver` param).
	fun fieldAccess(isSetter: Boolean, valueArg: BirExpression?): BirExpression = when {
		isSetter -> BirFieldSetExpression(ownerType = memberOwner, recv = recvExprIn(), name = name, value = checkNotNull(valueArg))
		// A field read carries the constructed-generic ret hint (a `tv`-typed field on `B<T>`) — parity with the
		// ordinary member field-read path (BirEmitterCalls).
		else -> BirFieldGet(ownerType = memberOwner, recv = recvExprIn(), name = name,
			ret = if ((memberOwner as? TypeNode.Fqn)?.args != null) birType(getterFn.returnType) else null)
	}
	fun staticPropertyAccess(isSetter: Boolean, valueArg: BirExpression?): BirExpression {
		val owner = memberOwner
		if (fieldBacked) return if (isSetter)
			BirStaticFieldSet(ownerType = owner, name = name, value = checkNotNull(valueArg))
		else BirStaticFieldGet(ownerType = owner, name = name, ret = vType)
		val kind = if (isSetter) "set" else "get"
		val fn = if (isSetter) setterFn!! else getterFn
		val args = listOfNotNull(valueArg)
		val argTypes = if (isSetter) listOf(vType) else emptyList()
		val ret = if (isSetter) TypeNode.Fqn("kotlin.Unit") else vType
		return BirCallStatic(ownerType = owner, method = name, prop = kind, argTypes = argTypes,
			ret = ret, args = args, declarationId = declarationIdForPhysicalAllocation(fn))
	}
	fun companionExtensionAccess(isSetter: Boolean, valueArg: BirExpression?): BirExpression {
		val owner = clrExternalOwner(prop) ?: fileClassOf(prop)
		val targetAccessor = if (isSetter) setterFn!! else getterFn
		// Deserialized properties can expose a synthetic backingField regardless of producer storage. Across a module
		// boundary only dll2klib's explicit ClrField marker is authoritative; source IR retains the real field fact.
		val storage = if (clrExternalOwner(prop) != null) isClrField(prop)
			else if (isSetter) writesAsStaticField(prop) else readsAsStaticField(prop)
		if (storage) {
			return if (isSetter)
				BirStaticFieldSet(TypeNode.Fqn(owner), name, checkNotNull(valueArg), companionReceiver = companionReceiver)
			else if (isLateinitProperty(prop))
				BirLateinitGet(ownerType = TypeNode.Fqn(owner), static = true, name = name, companionReceiver = companionReceiver)
			else BirStaticFieldGet(TypeNode.Fqn(owner), name, companionReceiver = companionReceiver)
		}
		val fn = targetAccessor
		val args = listOfNotNull(valueArg)
		return BirCallStatic(owner = BirField.Value(TypeNode.Fqn(owner)), method = name, prop = if (isSetter) "set" else "get",
			sig = overloadSignature(fn), args = args, companionReceiver = companionReceiver, declarationId = declarationIdForPhysicalAllocation(fn))
	}

	val readBody = BirReturn(when {
		companionExtension -> companionExtensionAccess(false, null)
		hasExtRecv -> extAccessorCall(false, null)
		staticProperty -> staticPropertyAccess(false, null)
		declClass != null && fieldBacked -> fieldAccess(false, null)
		declClass == null -> {
			// Top-level property: mirrors the ordinary top-level property-read path (a plain val/var is a static
			// field; a computed one — no backing field — is a get_<name>() static).
			// A property loaded from another module has no `IrFile` parent, so `fileClassOf` would fall back to the
			// file being emitted and name THIS module's facade. dll2klib carries the real physical file class on the
			// projected declaration; prefer it, exactly as the extension-property path above does.
			val owner = clrExternalOwner(prop) ?: fileClassOf(prop)
			if (readsAsStaticField(prop))
				BirStaticFieldGet(TypeNode.Fqn(owner), name)
			else BirCallStatic(owner = BirField.Value(TypeNode.Fqn(owner)), method = name, prop = "get",
				sig = overloadSignature(getterFn), args = emptyList(), declarationId = declarationIdForPhysicalAllocation(getterFn))
		}
		else -> accessorCall(false, null)
	})
	val readParams = if (unbound) listOf(BirParameter("receiver", recvTypeNode ?: OBJ)) else emptyList()
	val getMethod = BirFunctionDeclaration(name = "get", isStatic = false, isOverride = true, isVirtual = true,
		params = readParams, ret = vType, body = listOf(readBody), overrides = propertySlotOverrides("get"))
	// KProperty0/KProperty1's declared supertype `() -> V`/`(T) -> V` gives them a REAL fake-overridden `invoke`
	// abstract member (confirmed in the compiled BIR: `interfaces` drops the FunctionN supertype — a Kotlin
	// function type has no faithful CLR interface base — but the interface's OWN `methods` still carries the
	// fake override AS ITS OWN abstract slot). So the lifted class must implement it too, same body as `get`
	// (mirrors JVM's `PropertyReferenceImpl.invoke() = get()`).
	val invokeMethod = getMethod.copy(name = "invoke", overrides = propertySlotOverrides("invoke"))

	val mutableReference = ifaceSpec.name == "kotlin.reflect.KMutableProperty0" || ifaceSpec.name == "kotlin.reflect.KMutableProperty1"
	val setMethod = setterFn?.takeIf { mutableReference }?.let {
		val setBody = when {
			companionExtension -> kotc.bir.BirExpressionStatement(expr = companionExtensionAccess(true, kotc.bir.BirLocal(name = "value")))
			hasExtRecv -> kotc.bir.BirExpressionStatement(expr = extAccessorCall(true, kotc.bir.BirLocal(name = "value")))
			staticProperty -> kotc.bir.BirExpressionStatement(expr = staticPropertyAccess(true, kotc.bir.BirLocal(name = "value")))
			declClass != null && fieldBacked -> kotc.bir.BirExpressionStatement(expr = fieldAccess(true, kotc.bir.BirLocal(name = "value")))
			declClass == null -> {
				val owner = clrExternalOwner(prop) ?: fileClassOf(prop)
				if (writesAsStaticField(prop))
					kotc.bir.BirExpressionStatement(expr = kotc.bir.BirStaticFieldSet(ownerType = TypeNode.Fqn(owner), name = name, value = kotc.bir.BirLocal(name = "value")))
				else BirExpressionStatement(BirCallStatic(owner = BirField.Value(TypeNode.Fqn(owner)), method = name, prop = "set",
					sig = overloadSignature(setterFn!!), args = listOf(BirLocal("value")), declarationId = declarationIdForPhysicalAllocation(setterFn!!)))
			}
			else -> kotc.bir.BirExpressionStatement(expr = accessorCall(true, kotc.bir.BirLocal(name = "value")))
		}
		val setParams = readParams + BirParameter("value", vType)
		BirFunctionDeclaration(name = "set", isStatic = false, isOverride = true, isVirtual = true,
			params = setParams, ret = TypeNode.Fqn("kotlin.Unit"), body = listOf(setBody), overrides = propertySlotOverrides("set"))
	}

	// KCallable.name + KAnnotatedElement.annotations are NOT re-synthesized here: the lifted class extends the real
	// stdlib `kotlin.reflect.ClrPropertyStub<V>(name)` (which provides `name` + `annotations get()=emptyList()`),
	// so it only implements the KProperty0/1 slots ClrPropertyStub lacks (get/set/invoke). Consolidates onto the
	// stdlib impl instead of duplicating a bare-name `emptyList` call. The lift already implements KProperty<V> via
	// its KProperty0/1 interface, so the ClrPropertyStub<V>:KProperty<V> base is a CLR-legal diamond.
	val stubBase = TypeNode.Fqn("kotlin.reflect.ClrPropertyStub", listOf(vType))
	val stubBaseArg = BirConstant(type = TypeNode.Fqn("kotlin.String"), value = BirLiteral.TextLiteral(name))

	val recvFieldType = if (bound) birType(capturedRecv!!.type) else null
	val fields = if (bound) listOf(BirFieldDeclaration("__recv", recvFieldType!!)) else emptyList()
	val ctorParams = if (bound) listOf(BirParameter("__recv", recvFieldType!!)) else emptyList()
	val ctorBody = if (bound) listOf(BirSetField(TypeNode.Fqn(cname), BirThis(), "__recv", BirLocal("__recv"))) else emptyList()

	val methods = listOfNotNull(getMethod, invokeMethod, setMethod)
	// The lifted constructor delegates to ClrPropertyStub(String). Keep that Kotlin declaration identity beside
	// the arguments so bir2cir, which owns the CLR representation, can resolve and stamp the exact target ctor.
	val stubDelegationSig = listOf(TypeNode.Fqn("kotlin.String"))
	val definition = BirClassDefinition(name = cname, generated = true, companionCaptureOwner = companionCaptureOwner,
		typeParams = typeParameterDeclarations(freeTps).takeIf { freeTps.isNotEmpty() }, semanticOwner = activeSemanticOwner ?: fileClass,
		base = stubBase, interfaces = listOf(ifaceSpec), fields = fields,
		ctors = listOf(BirConstructor(ctorParams, listOf(stubBaseArg), ctorBody, delegationSig = stubDelegationSig)), methods = methods)
	liftedTypes.add(definition)

	val classType = if (freeTps.isEmpty()) TypeNode.Fqn(cname) else TypeNode.Fqn(cname, constructionTypeArgs)
	val ctorSig = if (bound) listOf(constructionRecvType!!) else emptyList()
	// This is the complete Kotlin-side fact bir2cir needs when the value later fills a function slot. It deliberately
	// carries the KProperty interface identity instead of asking the representation layer to rediscover semantics from
	// the generated class name, interfaces, or method body.
	val creation = BirNew(type = classType, argTypes = ctorSig, args = constructionArgs,
		companionCaptureOwner = companionCaptureOwner, propertyFunction = BirPropertyFunction(constructionPropertyInterface))
	if (staticReceiverEvaluation != null)
		kotc.bir.BirValueBlock(stmts = listOf(kotc.bir.BirExpressionStatement(expr = staticReceiverEvaluation)), result = creation)
	else creation
	} finally {
		freeTps.forEach { parameter ->
			val previous = savedTypeSubst[parameter]
			if (previous == null) typeArgSubst.remove(parameter) else typeArgSubst[parameter] = previous
		}
	}
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
internal fun isLocalDeclaration(d: IrDeclarationWithVisibility): Boolean =
	d.visibility.delegate == org.jetbrains.kotlin.descriptors.Visibilities.Local

/**
 * Index lexical local-function ownership before any BIR fact is rendered. Capture analysis and synthetic-type
 * discovery may ask for a ref-cell representation while the enclosing method signature/body is still being
 * prepared, before [localFunctionDecl] reaches the declaration statement. The declaration edge therefore has to be
 * available as a file-level semantic fact up front; bir2cir must not recover it later from a body or generated name.
 *
 * A lambda's IrSimpleFunction is carried by IrFunctionExpression rather than being a lexical `localFun` declaration.
 * Traverse its body to find declarations inside it, but do not assign the lambda function itself a localFun id.
 */
internal fun BirEmitter.indexLocalFunctionOwnership(file: IrFile) {
	fun register(fn: IrSimpleFunction) {
		val localId = localFunctionIds.getOrPut(fn) { "local${scopeCounter++}" }
		fn.body?.acceptVoid(object : IrVisitorVoid() {
			override fun visitElement(element: IrElement) {
				when (element) {
					is IrFunctionExpression, is IrSimpleFunction, is IrClass -> return
					is IrVariable -> localFunctionOwnerByVariable[element] = localId
				}
				element.acceptChildrenVoid(this)
			}
		})
	}
	file.acceptVoid(object : IrVisitorVoid() {
		override fun visitElement(element: IrElement) {
			when (element) {
				is IrFunctionExpression -> {
					element.function.body?.acceptChildrenVoid(this)
					return
				}
				is IrSimpleFunction -> if (isLocalDeclaration(element)) register(element)
			}
			element.acceptChildrenVoid(this)
		}
	})
}

/**
 * Render a second projection of one IR subtree with fresh lexical declaration ids. Default expressions are projected
 * once into their metadata carrier and may be projected again at same-module omitting call sites. Those are distinct
 * BIR declaration instances even when FIR/IR reuses the same [IrSimpleFunction] object; retaining the first ids would
 * put two `localFun` declarations with one id in the module. Keep the relation explicit in kotc and restore the primary
 * projection afterwards -- bir2cir must never infer clone boundaries from bodies or generated names.
 */
internal inline fun <T> BirEmitter.withClonedLocalFunctionIds(root: IrElement, block: () -> T): T {
	val functions = ArrayList<IrSimpleFunction>()
	val seenFunctions = java.util.Collections.newSetFromMap(
		java.util.IdentityHashMap<IrSimpleFunction, Boolean>(),
	)
	root.acceptVoid(object : IrVisitorVoid() {
		override fun visitElement(element: IrElement) {
			// IrFunctionExpression carries its function as a declaration payload rather than an ordinary child on this
			// visitor path. Enter the lambda body explicitly, matching indexLocalFunctionOwnership, so declarations in a
			// default lambda receive the carrier projection's ids too.
			if (element is IrFunctionExpression) {
				element.function.body?.acceptChildrenVoid(this)
				return
			}
			if (element is IrSimpleFunction && isLocalDeclaration(element) && seenFunctions.add(element))
				functions.add(element)
			element.acceptChildrenVoid(this)
		}
	})
	if (functions.isEmpty()) return block()

	val savedFunctionIds = java.util.IdentityHashMap<IrSimpleFunction, String>()
	val primaryVariableOwners = java.util.IdentityHashMap<IrVariable, String>()
	for (fn in functions) {
		// A default-value IR subtree may be materialized lazily and therefore need its declaration identity before the
		// file pre-indexer has encountered that exact IR object. Allocate BOTH identities here in kotc: the primary id
		// remains attached to the executable projection, while the fresh id is used only for this carrier projection.
		// This is declaration bookkeeping at the semantic producer, not a compatibility recovery in a lower layer.
		val primaryId = localFunctionIds.getOrPut(fn) { "local${scopeCounter++}" }
		savedFunctionIds[fn] = primaryId
		val cloneId = "local${scopeCounter++}"
		localFunctionIds[fn] = cloneId
		fn.body?.acceptVoid(object : IrVisitorVoid() {
			override fun visitElement(element: IrElement) {
				when (element) {
					is IrFunctionExpression, is IrSimpleFunction, is IrClass -> return
					is IrVariable -> {
						val primaryOwner = localFunctionOwnerByVariable[element] ?: primaryId
						primaryVariableOwners[element] = primaryOwner
						localFunctionOwnerByVariable[element] = cloneId
					}
				}
				element.acceptChildrenVoid(this)
			}
		})
	}
	try {
		return block()
	} finally {
		for ((fn, id) in savedFunctionIds) localFunctionIds[fn] = id
		for ((variable, owner) in primaryVariableOwners)
			localFunctionOwnerByVariable[variable] = owner
	}
}

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

/** Value declarations assigned (IrSetValue) anywhere under a node (for capture invariants). */
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
 * A capture normally keeps its own Kotlin name — that is what each projected capture has always emitted, and what a reader
 * expects to see — unless something else already owns that name in the same namespace, in which case it moves into a
 * `cap$` prefix Kotlin source cannot spell. Set [alwaysPrefix] where the namespace cannot be enumerated up front:
 * a local function's captures become PARAMETERS in its eventual MethodDef, and ilemit resolves a `{k:local}` read
 * against body locals before parameters in one flat map (Emitter.Expressions/Bodies), so a body local declared
 * anywhere inside that method would
 * shadow a like-named capture parameter from that point on. A lifted CLASS has no such problem: its captures are
 * FIELDS, read through `this`, and the names they must avoid are exactly its own fields and constructor parameters.
 *
 * Generated prefixes are a readable disjoint namespace for source identifiers, but callers must still seed [taken]
 * with every other name in the target frame because generated IR can introduce compiler-owned names. Downstream
 * consumers use the emitted field/parameter identity carried by the BIR use sites and never infer capture semantics
 * from a preferred spelling.
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
internal fun BirEmitter.capValueExpr(d: IrValueDeclaration): kotc.bir.BirExpression =
	// Evaluate the capture VALUE in the enclosing context, mirroring `exprInner`'s IrGetValue resolution ORDER:
	// captureSubst (an outer closure field / intrinsic-block binding), then `selfSubst` (an EXTENSION receiver bound to
	// its `__self`/`__recv` — an inline-lambda carrier RENAMES its ext receiver via selfSubst, so a lifted local fn /
	// nested closure that captures that receiver must pick up the SAME renamed local, NOT the raw `$this$<fn>` IR name;
	// without this a `buildString { … appendTwoDigits(x) … }` carrier passed `$this$buildString` verbatim while its
	// param was `__recvN` -> ilemit "load unknown var $this$buildString"), then `valSubst`, then the `<this>`/local
	// fallback. selfSubst/captureSubst are IDENTITY-keyed (the receiver by symbol, not name).
	captureSubst[d] ?: selfSubst[d] ?: valSubst[d]
		?: if (d.name.asString() == "<this>") kotc.bir.BirThis() else kotc.bir.BirLocal(localSlotName(d))

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
 *  (recv = A1, params = A2..An), so a lambda's `funcType` and its target use one physical delegate signature.
 *  For `context(A) B.(D) -> E` that puts the CONTEXT in `recv` and the real receiver in `params[0]`: the labels
 *  diverge from Kotlin's roles, but the PHYSICAL projection (`recv` prepended to `params` by `DelegateParams`) is
 *  the correct `A, B, D` — and physical agreement is the whole contract of this node. A non-extension context
 *  function type (`context(A) (D) -> E` = `Function2<A, D, E>`) has no recv and carries `[A, D]` in params.
 *  A `suspend` lambda sets `fn.suspend=true`, carrying the suspend FACT for the SM builder. bir2cir ERASES a suspend
 *  `fn` to `object` wherever it appears in a TYPE slot; only the `funcType` node key itself keeps it. */
internal fun BirEmitter.funcTypeOf(fn: IrSimpleFunction): TypeNode.Fn {
	val physical = orderedLambdaParams(fn).map { birTypeDeleg(it.type) }
	val isExtType = (extensionReceiverParam(fn) != null)
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
internal fun BirEmitter.lambdaParameters(params: List<IrValueParameter>, recvName: String? = null): List<BirLambdaParameter> =
	params.filter { it.kind != IrParameterKind.DispatchReceiver }
		// A `Unit`-typed PARAMETER must be the real Unit VALUE identity, not `void` (invalid metadata).
		.map { p ->
			val ty = if (p.type.isUnit()) TypeNode.Fqn("kotlin.Unit") else birTypeDeleg(p.type)
			val nm = if (recvName != null && p.kind == IrParameterKind.ExtensionReceiver) recvName else p.name.asString()
			BirLambdaParameter(nm, ty)
		}

/** Emit `block` with a lexical function's OWN extension receiver bound to `recvName`, so the body's `this` reads render
 *  as that parameter instead of `{k:this}`. `{k:this}` is the ENCLOSING instance (or nothing at all in a static
 *  lift), so without this the receiver was never reachable: `val f: Int.(Int) -> Int = { d -> this + d }` threw a
 *  NullReferenceException, and once context parameters joined the physical sequence `this` began reading the CONTEXT
 *  — physical slot 0 — and silently returned the wrong value. Saved and RESTORED: the same parameter may already be
 *  bound by an enclosing carrier. Local declarations use the same identity binding: captures may precede their
 *  receiver, and a nested declaration may capture that receiver separately from an enclosing dispatch receiver. */
internal inline fun <T> BirEmitter.withLambdaSelf(fn: IrSimpleFunction, recvName: String?, block: () -> T): T {
	val ext = extensionReceiverParam(fn)
	if (ext == null || recvName == null) return block()
	val had = selfSubst.containsKey(ext)
	val saved = selfSubst[ext]
	selfSubst[ext] = BirLocal(recvName)
	try { return block() } finally { if (had) selfSubst[ext] = saved!! else selfSubst.remove(ext) }
}

/** A fresh name for a lexical function's extension-receiver parameter, or null when it has none. Allocated against the
 *  function's own frame ([BirEmitter.freshFrameName]) — `{ __recv0 -> this + __recv0 }` is legal Kotlin, and a bare
 *  counter would have emitted two parameters called `__recv0`. */
internal fun BirEmitter.lambdaRecvName(fn: IrSimpleFunction): String? =
	if ((extensionReceiverParam(fn) != null)) freshFrameName("__recv", fn) else null

/** Project a local function as a lexical BIR declaration; captures remain explicit leading parameters. */
internal fun BirEmitter.localFunctionDecl(fn: IrSimpleFunction): BirLocalFunction {
	// This is a BIR declaration identity, not a CLR method name. InlineSplice alpha-renames cloned ids so they remain
	// unique within the BIR file; bir2cir assigns a fresh compiler-reserved physical name per materialization.
	val localId = localFunctionIds[fn] ?: error("local function ownership was not indexed before BIR emission")
	// Captured vars (incl. the enclosing `this`) become leading params; the call site prepends their values.
	val captures = capturedVars(fn, includeThis = true)
	// A local fn's OWN value parameters = its regular params PLUS its dispatch/extension receiver. A callable-reference
	// ADAPTER (`ADAPTER_FOR_CALLABLE_REFERENCE`) whose bound receiver is an ExtensionReceiver param `receiver` references
	// that param by name in its body, so it MUST be emitted as a leading method param, in declaration order (receivers
	// precede regulars); filtering to Regular alone drops it and orphans a `receiver.f(p0)` body ref — the dangling-
	// `receiver` IrSanity fault (kotc consumes the RAW fir2ir form, before UpgradeCallableReferences would flatten
	// receivers to Regular). Captures are DISTINCT (outer decls, prepended before these). Bind the declaration's own
	// extension receiver by IR identity, not its `<this>` spelling or position among those captures.
	// The ORDER is the call site's: receivers (dispatch then extension) ahead of the [isValueParameter] sequence
	// (contexts then regulars), because the caller emits `capArgs + recvArgs + filledArgs(call)` in exactly that shape.
	// IR order would interleave a context parameter BETWEEN the two receivers (fir2ir orders dispatch/context/extension/
	// regular), which for a `context(c) fun T.f()` local would put the context value in the extension receiver's slot.
	val ownValueParams = localFunctionOwnParams(fn)
	val recvName = lambdaRecvName(fn)
	fun ownParamName(p: IrValueParameter) =
		if (p.kind == IrParameterKind.ExtensionReceiver) recvName!! else p.name.asString()
	// A local fn referencing an enclosing type parameter (in a capture, its own params/receivers, its return, or a TYPE
	// OPERAND in its body such as `x is R` / `R::class`) becomes a GENERIC static method — reified CLR generics, same as
	// a capturing closure class, which scans `bodyTypeOperands` for exactly the same reason. The call site (callStatic)
	// passes the enclosing type params as type arguments. Missing a body-only operand here would also leave a `tv` this
	// lift cannot name in its own frame (see `_syntheticTypeArgs` below).
	val freeTps = freeTypeParams(
		captures.map { it.type } + ownValueParams.map { it.type } + listOf(fn.returnType) + bodyTypeOperands(fn))
	localFns[fn] = BirEmitter.LocalFunctionFact(localId, captures, freeTps)
	// Preserve the declaration-to-enclosing-frame relation before giving the declaration an independent dense frame.
	// These values are not declaration identities: nested functions can both legitimately own `method#0`.
	val syntheticOrigins = freeTps.map { typeArgSubst[it] ?: tvOf(it) }
	fun pj(name: String, t: IrType) = BirParameter(name, birType(t))
	// This lift puts the captures in the method's PARAMETER namespace, beside the fn's own value params — and a
	// transitive capture arrives from ANOTHER body, where its name may resolve to a different declaration than it does
	// here (`fun bump() { n++ }` called by `fun addTwice(n: Int)`). Uniquing keeps the wrong binding from happening.
	val capPairs = uniqueCaptureNames(
		captures, ownValueParams.mapTo(HashSet(), ::ownParamName), alwaysPrefix = true)
	// Captures arrive as leading params; rewrite body refs to those params. This must cover not only `<this>` but
	// also receiver-like captured params such as `$this$buildString`, otherwise an active inline substitution can
	// leak a caller-local (`__lam<N>`) into the lifted method body.
	// Save any prior substitution and RESTORE it after the body, exactly as the closure path does: this local fun may
	// be declared INSIDE a closure/object/local class that already bound the same outer decl to its own capture field.
	// Removing the binding instead would leave the enclosing frame reading the bare local again, so the `bump()` call
	// site after it (capValueExpr) emits a local that does not exist in that frame.
	val savedCaptureSubst = capPairs.associate { (decl, _) -> decl to captureSubst[decl] }
	val savedCaptureNames = capPairs.associate { (decl, _) -> decl to captureLocalName[decl] }
	// Register cell identities before changing frames, but render their arguments in the receiving method's frame.
	capPairs.forEach { (decl, _) -> if (isRefCell(decl) || (decl is IrVariable && decl.isVar)) refTypeName(decl) }
	val savedTypeSubst = freeTps.associateWith { typeArgSubst[it] }
	freeTps.forEachIndexed { index, parameter -> typeArgSubst[parameter] = TypeNode.Tv("method", index) }
	capPairs.forEach { (decl, fname) ->
		captureSubst[decl] = BirLocal(fname)
		captureLocalName[decl] = fname
	}
	val capParams: List<BirParameter>
	val ownParams: List<BirParameter>
	val body: List<BirStatement>
	val ret: TypeNode
	val typeParams: List<TypeParameterDeclaration>?
	try {
		capParams = capPairs.map { (decl, fname) ->
			val type = captureFieldType(decl)
			val shared = if (decl is IrVariable && decl.isVar && !isRefCell(decl)) refType(decl) else null
			BirParameter(fname, type, sharedCellType = shared)
		}
		ownParams = ownValueParams.map { pj(ownParamName(it), it.type) }
		body = withLambdaSelf(fn, recvName) {
			(fn.body as? IrBlockBody)?.statements.orEmpty().flatMap { stmt(it) }
		}
		ret = birType(fn.returnType)
		typeParams = typeParameterDeclarations(freeTps).takeIf { freeTps.isNotEmpty() }
	} finally {
		capPairs.forEach { (decl, _) ->
			val previous = savedCaptureSubst[decl]
			if (previous != null) captureSubst[decl] = previous else captureSubst.remove(decl)
			val previousName = savedCaptureNames[decl]
			if (previousName != null) captureLocalName[decl] = previousName else captureLocalName.remove(decl)
		}
		freeTps.forEach { parameter ->
			val previous = savedTypeSubst[parameter]
			if (previous != null) typeArgSubst[parameter] = previous else typeArgSubst.remove(parameter)
		}
	}
	// The declaration has an independent dense method frame, but bir2cir must still partition those slots between the
	// eventual CLR owner's type parameters and the moved method's remaining parameters. Preserve that positional
	// relation to the lexical frame explicitly. Two source parameters owned by different declarations can both have
	// source index 0, so deriving it later from source indices would alias them. This is consumed before CIR.
	val synthTvs = syntheticOrigins.takeIf { freeTps.isNotEmpty() }
	// The nested declaration itself is the ownership fact. The declaration id is referenced only by callLocal /
	// localFunRef nodes in this lexical subtree; neither a FileClass nor a CLR owner is named in BIR.
	val decl = BirFunctionDeclaration(sourceName = fn.name.asString(), isStatic = true, isOverride = false, isVirtual = false,
		typeParams = typeParams, syntheticTypeArgs = synthTvs, mods = functionModifiers(fn), suspendRet = ret.takeIf { fn.isSuspend },
		params = capParams + ownParams, ret = ret, body = body)
	return BirLocalFunction(localId, decl)
}

/**
 * Lift a function-local class to a top-level synthetic type. Referenced outer locals (incl. the enclosing
 * `this`) become leading ctor params / capture fields; construction sites prepend those values (see the
 * IrConstructorCall handler). Returns a no-op statement (the declaration emits nothing inline).
 */
internal fun BirEmitter.liftLocalClass(klass: IrClass): kotc.bir.BirStatement {
	if (anonNames.containsKey(klass)) return kotc.bir.BirBlock(emptyList())   // already lifted
	val cname = "dotkt\$${klass.name.asString()}\$${scopeCounter++}"
	anonNames[klass] = cname
	val captured = capturedVarsForObject(klass)
	// Writing a captured outer local from the class is heap ref-celled the SAME way the lambda/object path is: the
	// module-wide ref-cell scan (BirEmitter.initRefCells, run before ANY file is emitted) promoted every
	// captured-and-mutated `var` to a shared `dotkt$Ref<T>`, so `isRefCell(it)` is true here whatever root we are
	// under — a method, a constructor/init block, an initializer expression — and the class reads/writes the shared
	// cell. The shape is SUPPORTED; reaching the branch below means an assigned capture was not a mutable local, which valid
	// frontend IR cannot produce: a Kotlin parameter cannot be assigned.
	if (captured.any { it in mutatedIn(klass) && !isRefCell(it) })
		return kotc.bir.BirExpressionStatement(invariantBroken(klass,
			"a local class writes a captured outer variable that was not promoted to a heap ref-cell"))
	val capPairs = captureFieldPairs(klass, captured)
	// Save any prior binding and RESTORE it below rather than dropping it — this local class may be declared inside a
	// closure/object/local fun that already bound the same outer decl to its own capture field (mirrors the closure
	// path); dropping it leaves the enclosing frame reading a bare local it no longer has.
	val savedSubst = capPairs.associate { (decl, _) -> decl to captureSubst[decl] }
	capPairs.forEach { (decl, fname) ->
		captureSubst[decl] = kotc.bir.BirFieldGet(ownerType = TypeNode.Fqn(cname), recv = kotc.bir.BirThis(), name = fname)
	}
	// A local class that references an enclosing type parameter (in a capture field, its supertype/bounds, or a member
	// signature/body operand) is lifted GENERICALLY over those params — reified CLR generics, exactly as the lifted
	// object-literal path (blockExpr) does. `captureEnclosingGenerics` runs typeDef's structural scan, which records
	// `liftedTypeArgParams[klass]`; each `new L()` site (IrConstructorCall) instantiates the flattened class with the
	// enclosing args from that record. A non-generic local class captures nothing and lifts unchanged.
	// Register BEFORE emitting the class: a member of this class may CONSTRUCT it (or a subclass may delegate to its
	// ctor) while `typeDef` is running, and that construction site reads this record to prepend the capture arguments.
	localClassCaptures[klass] = captured
	liftedTypes.add(sourceClassDefinition(klass, capPairs, captureEnclosingGenerics = true, generated = true))
	capPairs.forEach { (decl, _) -> val prev = savedSubst[decl]; if (prev != null) captureSubst[decl] = prev else captureSubst.remove(decl) }
	return kotc.bir.BirBlock(emptyList())
}
