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

/** A loop label (Kotlin `outer@`) as JSON, or null. break/continue target loops by this label. */
internal fun BirEmitter.labelJson(label: String?): String = label?.let { str(it) } ?: "null"

/** A loop body: a block's statements, or a single bare statement (single-statement loop bodies). */
internal fun BirEmitter.loopBody(body: IrExpression?): String = when (body) {
	null -> ""
	is IrBlock -> body.statements.joinToString(",") { stmt(it) }
	else -> stmt(body)
}

/** Wrap a statement-position control transfer ([xfer] = a `goto`/`break`/`continue` node) so it can sit in an
 *  EXPRESSION slot (a `break`/`continue` used as an `if`/`when` branch value). The transfer runs first and jumps
 *  away; the `throw null` result is unreachable dead code that gives the valueBlock a well-formed result which
 *  never falls through to the surrounding merge — so the merge keeps only the live branch's type. */
internal fun BirEmitter.breakContinueExpr(xfer: String): String =
	valueBlockJson(type = null, stmts = xfer, result = unreachableResultJson())

/** The result of a valueBlock that never falls through (a control transfer already left it): `throw null`, dead code
 *  the surrounding merge must not take a type from. Typed `kotlin.Unit`, so it is not a null VALUE. */
private fun BirEmitter.unreachableResultJson(): String =
	"""{"k":"throwExpr","value":{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}}"""

/** The standard tail-call optimization: a self-tail-call in a `tailrec` fn becomes a back-jump to the method's
 *  entry after reassigning the parameters to the call's arguments (Kotlin/JVM's own `tailrec` lowering, which our
 *  pipeline skips because it runs Fir2Ir straight into our backend, no JVM lowerings — so without this deep tail
 *  recursion overflows the CLR stack; §2b). The call sits in an EXPRESSION slot (`return f(...)`, or a `when`/`if`
 *  branch feeding the return), so we emit a `valueBlock`: evaluate every argument into a temp FIRST (so a later arg
 *  reading an earlier param — `f(n-1, acc+n)` — is not corrupted by the reassignment), reassign each param (a
 *  `setLocal` on a param name emits `starg`), then `goto` the entry label. The block's result is an unreachable
 *  `throwExpr` (the jump already left), mirroring [breakContinueExpr] — the surrounding `return` never executes. */
internal fun BirEmitter.tailrecJump(call: IrCall, ctx: BirEmitter.TailrecCtx): String {
	data class Reassign(val name: String, val tmp: String, val valueJson: String, val type: org.jetbrains.kotlin.ir.types.IrType)
	val reassigns = ArrayList<Reassign>()
	ctx.fn.parameters.forEachIndexed { i, p ->
		// The dispatch receiver of a member `tailrec` self-call is the SAME `this` (Kotlin requires it) — never reassigned.
		if (p.kind == IrParameterKind.DispatchReceiver) return@forEachIndexed
		val arg = call.arguments.getOrNull(i) ?: return@forEachIndexed
		val name = if (p.kind == IrParameterKind.ExtensionReceiver) extensionReceiverSlotName(ctx.fn) else p.name.asString()
		reassigns.add(Reassign(name, "__tailrec_${ctx.startLabel}_$i", coerceValue(arg, p.type), p.type))
	}
	val stmts = ArrayList<String>()
	reassigns.forEach { stmts.add("""{"k":"var","name":${str(it.tmp)},"type":${birType(it.type).toJson()},"init":${it.valueJson}}""") }
	reassigns.forEach { stmts.add("""{"k":"setLocal","name":${str(it.name)},"value":{"k":"local","name":${str(it.tmp)}}}""") }
	stmts.add("""{"k":"goto","id":${ctx.startLabel}}""")
	return valueBlockJson(type = null, stmts = stmts.joinToString(","), result = unreachableResultJson())
}

/** `while(c){B}` -> CFG block: `START: if(!c) goto END; B; goto START; END:`. continue->START, break->END. */
internal fun BirEmitter.cfgWhile(node: IrWhileLoop): String {
	val start = cfgFresh(); val end = cfgFresh()
	cfgLoopStack.add(Triple(node, start, end))
	val body = loopBody(node.body)
	cfgLoopStack.removeAt(cfgLoopStack.size - 1)
	val parts = ArrayList<String>()
	parts.add("""{"k":"label","id":$start}""")
	parts.add("""{"k":"brIf","id":$end,"on":false,"cond":${expr(node.condition)}}""")
	body.takeIf { it.isNotEmpty() }?.let { parts.add(it) }
	parts.add("""{"k":"goto","id":$start}""")
	parts.add("""{"k":"label","id":$end}""")
	return """{"k":"block","body":[${parts.joinToString(",")}]}"""
}

/** `do{B}while(c)` -> CFG: `START: B; CONT: if(c) goto START; END:`. continue->CONT, break->END. */
internal fun BirEmitter.cfgDoWhile(node: IrDoWhileLoop): String {
	val start = cfgFresh(); val cont = cfgFresh(); val end = cfgFresh()
	cfgLoopStack.add(Triple(node, cont, end))
	val body = loopBody(node.body)
	cfgLoopStack.removeAt(cfgLoopStack.size - 1)
	val parts = ArrayList<String>()
	parts.add("""{"k":"label","id":$start}""")
	body.takeIf { it.isNotEmpty() }?.let { parts.add(it) }
	parts.add("""{"k":"label","id":$cont}""")
	parts.add("""{"k":"brIf","id":$start,"on":true,"cond":${expr(node.condition)}}""")
	parts.add("""{"k":"label","id":$end}""")
	return """{"k":"block","body":[${parts.joinToString(",")}]}"""
}

/** A Kotlin `for (x in a..b / array)` -> a BIR counter loop / indexed array loop, or null. */
internal fun BirEmitter.birForLoop(block: IrBlock): String? {
	val iterVar = block.statements.getOrNull(0) as? IrVariable
	val whileLoop = block.statements.getOrNull(1) as? IrWhileLoop
	val bodyBlock = whileLoop?.body as? IrBlock
	val loopVar = bodyBlock?.statements?.getOrNull(0) as? IrVariable
	if (iterVar == null || bodyBlock == null || loopVar == null) return null
	val source = (iterVar.initializer as? IrCall)?.let { dispatchReceiver(it) ?: extensionReceiver(it) }
	val body = bodyBlock.statements.drop(1).joinToString(",") { stmt(it) }
	val lbl = labelJson(whileLoop.label)
	// `for (x in array)` -> an indexed loop (avoids the kotlin iterator types). No `elem`: bir2cir derives the
	// loop-variable element type off the array operand's (now faithful) type.
	if (source != null && isArrayType(source.type))
		return """{"k":"forArray","label":$lbl,"var":${str(localSlotName(loopVar))},"array":${expr(source)},"body":[$body]}"""
	// NON-array for-loops: kotc no longer classifies the source at all — whether it is a counted RANGE, an
	// `a downTo b` counter, a stdlib collection, a `kotlin.sequences.Sequence`, or a dll2klib-projected .NET
	// enumerable are each a `kotlin.ranges.*`/`kotlin.collections.*` FQN, a `downTo` operator identity, or a
	// .NET-type / `@Clr` resolution against the reference assemblies — a Kotlin<->CLR relation that belongs in
	// bir2cir. kotc emits ONE faithful `forIn` for EVERY non-array source: the FAITHFUL source + its runtime type
	// token (`srcType`) + the element type (`elem`, a pure Kotlin loop-var fact) + the loop body, plus the
	// `fallback` = the FIR-desugared iterator-protocol block (what kotc used to emit by returning null here).
	// bir2cir's ForInLowering dispatches it: a counted range (IntRange, or IntProgression in a stdlib self-build)
	// -> `forRange`; an `a downTo b` in a consumer build -> a counted `for`; a `kotlin.sequences.Sequence` or a
	// .NET enumerable (any build), or a stdlib collection in a stdlib self-build -> `forEachInline` (GetEnumerator);
	// anything else -> the `fallback`. NO CLR/stdlib classification leaves kotc.
	//
	// `elem` = the source's first type arg, else the loop var's type — bir2cir reads it verbatim when it turns a
	// `forIn` into `forEachInline`.
	val elem = (source?.type as? IrSimpleType)?.arguments?.firstOrNull()
		?.let { (it as? IrTypeProjection)?.type }?.let(::birType) ?: birType(loopVar.type)
	if (source != null)
		return """{"k":"forIn","label":$lbl,"elem":${str(elem)},"src":${expr(source)},"srcType":${birType(source.type).toJson()},"var":${str(localSlotName(loopVar))},"body":[$body],"fallback":{"k":"block","body":[${block.statements.joinToString(",") { stmt(it) }}]}}"""
	return null
}

internal fun BirEmitter.tryStmt(node: IrTry): String {
	val catches = node.catches.joinToString(",") { c ->
		val p = c.catchParameter
		// Use birType so a USER exception class catches as its own type (`@AppErr`), not `object`
		// (which degrades to System.Object — an unverifiable catch).
		"""{"excType":${birType(p.type).toJson()},"var":${str(localSlotName(p))},"body":[${bodyStmts(c.result)}]}"""
	}
	val finally = node.finallyExpression?.let { ""","finally":[${bodyStmts(it)}]""" } ?: ""
	return """{"k":"try","type":${birType(node.type).toJson()},"body":[${bodyStmts(node.tryResult)}],"catches":[$catches]$finally}"""
}

internal fun BirEmitter.bodyStmts(e: IrExpression): String =
	if (e is IrBlock) e.statements.joinToString(",") { stmt(it) } else stmt(e)

/** `try`/`catch` in value position -> a temp local assigned in each branch, returned via a valueBlock. */
internal fun BirEmitter.tryExpr(node: IrTry): String {
	val tv = "dotkt\$tryval${scopeCounter++}"
	var anyNull = false
	val (tryBody, tryNull) = assignBranch(node.tryResult, tv)
	if (tryNull) anyNull = true
	val catches = node.catches.joinToString(",") { c ->
		val p = c.catchParameter
		val (body, catchNull) = assignBranch(c.result, tv)
		if (catchNull) anyNull = true
		// birType (matching tryStmt) so the catch type stays the Kotlin FQN that bir2cir lowers via @ClrTypeAlias —
		// a USER exception class catches as its own `@AppErr`, a stdlib one as its BCL alias.
		"""{"excType":${birType(p.type).toJson()},"var":${str(localSlotName(p))},"body":[$body]}"""
	}
	val finally = node.finallyExpression?.let { ""","finally":[${bodyStmts(it)}]""" } ?: ""
	val tryS = """{"k":"try","type":${fqnJson("kotlin.Unit")},"body":[$tryBody],"catches":[$catches]$finally}"""
	// The branches all assign into the shared temp `tv`, so `tv`'s declared type IS this join's Kotlin type.
	// `joinNullBranch` is the FRONTEND FACT beside it: the frontend resolved the join to a NON-nullable type while
	// some branch's result may be `null` — the substituted-generic / stdlib-inline-splice shape that drops the `?`
	// (#56/#126). Whether the physical slot must then widen to `Nullable<V>` is a question about VALUE-ness, and
	// bir2cir's ValueJoinNullWidening answers it. The fact rides the declaration this emitter just MINTED, which is
	// what makes it a statement about this join and not a guess about some local that happens to sit nearby.
	// It also rides the valueBlock ROOT, which carries no type and so widens nothing: that copy is what lets an
	// ENCLOSING join see this one as a null-yielding branch (see [emittedYieldsNull]). The `result` here is a read of
	// the temp, so the root fact cannot be re-derived from it — this join states it directly.
	val bt = birType(node.type)
	val nullBranch = bt !is TypeNode.Nullable && anyNull
	val varFact = if (nullBranch) ""","joinNullBranch":true""" else ""
	return valueBlockJson(
		type = null,
		stmts = """{"k":"var","name":${str(tv)},"type":${bt.toJson()}$varFact},$tryS""",
		result = """{"k":"local","name":${str(tv)}}""",
		yieldsNull = nullBranch,
	)
}

/** Mint a `valueBlock`, stamping `joinNullBranch` on its ROOT exactly when its VALUE may leave an enclosing join null.
 *
 *  A `valueBlock` is the emitter's one PASS-THROUGH wrapper: its statements are side effects and its `result` IS the
 *  node's value, so it yields null exactly when that result does. Every site that mints one goes through here, which
 *  is what lets [emittedYieldsNull] stay a ROOT test instead of re-reading the emitted JSON. The `yieldsNull`
 *  override exists for the one producer whose verdict is not readable from `result` — [tryExpr], whose result is a
 *  read of the temp its branches assigned into. */
internal fun BirEmitter.valueBlockJson(
	type: String?,
	stmts: String,
	result: String,
	yieldsNull: Boolean = emittedYieldsNull(result),
): String {
	val fact = if (yieldsNull) """"joinNullBranch":true,""" else ""
	val ty = if (type != null) """"type":$type,""" else ""
	return """{"k":"valueBlock",$fact$ty"stmts":[$stmts],"result":$result}"""
}

/** Like [bodyStmts], but the branch's final value-expression is assigned to `tv` (a value already throws/returns
 *  -> emitted as-is). For try-as-expression: each branch leaves its result in the temp. Returns the emitted
 *  statements paired with whether this branch MAY LEAVE THE JOIN NULL — decided on the EMITTED result, because that
 *  is where a nested join's own verdict and the emitter's own `valueBlock` wrapping are visible; [emittedYieldsNull]
 *  states which emitted shapes count. A bare `null` literal is `Nothing?`-typed, so it is emitted as a plain
 *  statement and assigns nothing at all: the temp keeps its default, which is exactly why that default has to be
 *  able to BE null. */
internal fun BirEmitter.assignBranch(e: IrExpression, tv: String): Pair<String, Boolean> {
	val stmts = if (e is IrBlock) e.statements else listOf(e)
	val pre = stmts.dropLast(1).joinToString(",") { stmt(it) }
	val last = stmts.lastOrNull()
	var yieldsNull = false
	val tail = when {
		last is IrExpression && !last.type.isUnit() && last.type.classFqName?.asString() != "kotlin.Nothing" -> {
			val value = expr(last)
			yieldsNull = emittedYieldsNull(value)
			"""{"k":"setLocal","name":${str(tv)},"value":$value}"""
		}
		last != null -> stmt(last).also { yieldsNull = emittedStmtYieldsNull(it) }
		else -> ""
	}
	return listOf(pre, tail).filter { it.isNotEmpty() }.joinToString(",") to yieldsNull
}

/**
 * Statement-position `if`/`when` -> CFG branches (E-0.5 §5.3): each non-else branch is
 * `brIf NEXT (!cond); body; goto END; NEXT:`; the else body falls through to `END:`. Expression-position
 * if/when keep the value form (`ternary`, via expr). Mixes freely with CFG loops (break/return inside work).
 */
internal fun BirEmitter.cfgWhen(node: IrWhen): String {
	val end = cfgFresh()
	val parts = ArrayList<String>()
	for (br in node.branches) {
		val isElse = (br.condition as? IrConst)?.value == true
		if (isElse) {
			stmt(br.result).takeIf { it.isNotEmpty() }?.let { parts.add(it) }
		} else {
			val next = cfgFresh()
			parts.add("""{"k":"brIf","id":$next,"on":false,"cond":${expr(br.condition)}}""")
			stmt(br.result).takeIf { it.isNotEmpty() }?.let { parts.add(it) }
			parts.add("""{"k":"goto","id":$end}""")
			parts.add("""{"k":"label","id":$next}""")
		}
	}
	parts.add("""{"k":"label","id":$end}""")
	return """{"k":"block","body":[${parts.joinToString(",")}]}"""
}

/**
 * Bind a subject/receiver expression for exactly-ONCE evaluation before splicing it into several use sites.
 * A STABLE expression splices directly — re-reading it is free and side-effect-free. Anything else gets a
 * temp local (returned as a `var` statement for a wrapping valueBlock) and the use sites splice the local
 * READ: splicing the rendered initializer JSON itself re-evaluates it per splice (the when-subject /
 * safe-call / range-membership double-eval defect).
 * The stability question has ONE implementation, [isStableValue] — the same one the call-evaluation plan
 * records as a binding's `stable`. This site asks it about a splice rather than about a binding, but it is
 * the same question ("re-readable, and free to move past another value"), so it must not be re-derived here.
 * Returns (varStmtJson or null-if-stable, useJson). Only safe where the expression is suspension-free —
 * expression position is; a suspend call there just renders plainly with `"suspendCall":true` for bir2cir.
 */
internal fun BirEmitter.bindOnce(init: IrExpression, type: IrType, prefix: String): Pair<String?, String> {
	if (isStableValue(init)) return null to expr(init)
	val tv = "$prefix${scopeCounter++}"
	// A FLEXIBLE/PLATFORM generic-param subject `T!` (`{t:oblivious,of:tv}`, a .NET generic's un-annotated member, #8)
	// must NOT become a `gp:T` local: `!T` cannot hold null when T is instantiated with a value type, and the `isinst`
	// REF result stored into a `!T` slot is unverifiable ([found ref 'T'][expected value 'T'] — the stdlib's documented
	// "never hold a V? in a local" rule, ClrMapDefaults.kt). Erase to object: every use site of such a subject is
	// ref-typed (objEq null-check / objMethod / ref member).
	// The NULLABLE twin (`{t:nullable,of:tv}`, `x as? T`) is NOT decided here: `Nullable(Tv)` is object-erased at
	// every slot — body locals included — by bir2cir's uniform erasure (#86), which is where a CLR-representation
	// decision belongs. `Oblivious(Tv)` is not part of that family (an open platform `T!` reaching a REFERENCED .NET
	// generic's `!T` member must stay `!T` there), so its subject-temp erasure still has no bir2cir counterpart.
	val bt = birType(type)
	val vt = if (bt is TypeNode.Oblivious && bt.of is TypeNode.Tv) OBJ else bt
	return """{"k":"var","name":${str(tv)},"type":${vt.toJson()},"init":${expr(init)}}""" to
		"""{"k":"local","name":${str(tv)}}"""
}

internal fun BirEmitter.blockExpr(block: IrBlock): String {
	// `object : I { … }` -> a synthetic named class (lifted) + `new`. Instance fields are real fields;
	// captured outer values (incl. the enclosing `this`) become extra ctor params / capture fields.
	if (block.origin?.toString() == "OBJECT_LITERAL") {
		val anon = block.statements.filterIsInstance<IrClass>().firstOrNull()
		if (anon != null) {
			// A member call can ask for the anonymous receiver's owner before it renders the receiver expression.
			// typeName therefore owns the one-time allocation; reuse it here instead of minting a second identity.
			val cname = typeName(anon)
			val captured = capturedVarsForObject(anon)
			// Writing an outer local through the object goes through its heap ref-cell: the module-wide scan
			// (BirEmitter.initRefCells) already promoted every captured-and-mutated `var`, so `isRefCell(it)` holds
			// here — the shape is SUPPORTED. Reaching the branch below means the scan and this predicate disagree
			// (they read the same two helpers over the same node), i.e. a mutated capture that is not a `var` local,
			// which valid frontend IR cannot produce: a Kotlin parameter cannot be assigned.
			if (captured.any { it in mutatedIn(anon) && !isRefCell(it) })
				return invariantBroken(block, "an object expression writes a captured outer variable that was not " +
					"promoted to a heap ref-cell")
			// Capturing an ENCLOSING TYPE PARAMETER (`fun <T> mk(v:T) = object : Box<T> { ... }`, or an inlined object
			// whose supertype/captures resolve to the enclosing `T`): typeDef makes the synthesized class GENERIC over
			// the params its members reference (reified CLR generics), recording them in `liftedTypeArgNames`. The `new`
			// site must then INSTANTIATE it with the enclosing args — bracket those `gp:` tokens onto the constructed type
			// (they resolve at THIS site, i.e. the enclosing method/type scope). Mirrors newClosure/newSam's `typeArgs`.
			val capPairs = captureFieldPairs(anon, captured)
			// Save any PRIOR binding for each captured decl: when this object literal is nested inside a capturing
			// closure/object that captures the SAME outer var (`element`), the enclosing frame already bound it to
			// its OWN field. Blindly `remove`ing after typeDef would clobber that, so the capture VALUE below would
			// mis-render as a bare `local element` (the enclosing `this.element` is out of scope at the `new` site ->
			// ilemit "load unknown var"). Restore the prior binding instead — mirrors the closure path (lambda()).
			val savedSubst = capPairs.associate { (decl, _) -> decl to captureSubst[decl] }
			capPairs.forEach { (decl, fname) ->
				captureSubst[decl] = """{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":${str(fname)}}"""
			}
			liftedTypes.add(typeDef(anon, capPairs, captureEnclosingGenerics = true, generated = true))
			capPairs.forEach { (decl, _) -> val prev = savedSubst[decl]; if (prev != null) captureSubst[decl] = prev else captureSubst.remove(decl) }
			// Instantiate the flattened generic anon with the captured params rendered in THIS (enclosing) scope
			// (`liftedCaptureArgs` honors any active inline `typeArgSubst`, else the enclosing `tv`) — shared with the
			// var-slot birType / member-access ownerSpec so all three name the SAME `dotkt$objN<T>`.
			val liftedCaps = liftedCaptureArgs(anon)
			// Capture values are evaluated in the OUTER context (this frame's captureSubst restored above).
			val capArgs = captured.joinToString(",") { capValueExpr(it) }
			val capArgTypes = capPairs.joinToString(",") { (decl, _) -> str(captureFieldType(decl)) }
			val newType = if (liftedCaps.isEmpty()) TypeNode.Fqn(cname) else TypeNode.Fqn(cname, liftedCaps)
			return """{"k":"new","type":${newType.toJson()},"argTypes":[$capArgTypes],"args":[$capArgs]}"""
		}
	}
	// `when (subject)` lowers to `{ val tmp = subject; WHEN }` in expression position. The subject is bound
	// ONCE (bindOnce): the old code stored valSubst[key] = the RENDERED initializer JSON, so every IrGetValue
	// of the subject re-spliced — and re-EVALUATED — it (a when-subject call ran once per branch test; a
	// safe-call receiver ran twice).
	val tmp = block.statements.getOrNull(0) as? IrVariable
	val whenExpr = block.statements.getOrNull(1) as? IrWhen
	if (block.statements.size == 2 && tmp != null && whenExpr != null && tmp.initializer != null) {
		val key = tmp.name.asString()
		val origin = block.origin?.toString()
		// Save/restore (not remove) the key: a nested same-named subject must not clobber the outer splice.
		val saved = valSubst[key]
		fun restore() { if (saved != null) valSubst[key] = saved else valSubst.remove(key); valSubstUnwrapped.remove(key) }
		// `a?.member` where member is a value type -> Nullable<T>: bind `a` once, then null-gate. A nullable
		// VALUE-type receiver (`Char?`) is gated by HasValue and the member sees the UNWRAPPED .Value (the
		// ELVIS shape below) — splicing the raw Nullable<T> where the element is required emitted invalid IL
		// (e.g. `conv int` over a Nullable<char> -> InvalidProgramException).
		if (origin == "SAFE_CALL") nullableElem(block.type)?.let { elem ->
			val (subjVar, subj) = bindOnce(tmp.initializer!!, tmp.type, "__nv")
			val recvElem = nullableElem(tmp.type)
			// #198: when the accessed member is ITSELF value-nullable (`b?.n` with `val n: Int?`), `b.n` already
			// evaluates to a `Nullable<T>`, and `b?.n` flattens to that SAME `Nullable<T>` (elem equality is
			// guaranteed - both this member type and block.type carry the same element). Wrapping it in
			// `nullableWrap` would `newobj Nullable<T>(Nullable<T>)` (ilemit EmitNativeClrNullableWrap over an
			// already-nullable value) -> InvalidProgram. Emit the member verbatim in the present arm instead.
			val memberAlreadyNullable = nullableElem(whenExpr.branches.last().result.type) != null
			fun present(member: String) =
				if (memberAlreadyNullable) member else """{"k":"nullableWrap","elem":${str(elem)},"e":$member}"""
			val core: String
			if (recvElem != null) {
				valSubst[key] = """{"k":"nullableValue","elem":${str(recvElem)},"e":$subj}"""
				valSubstUnwrapped.add(key)   // receiver already reads .Value -> the value-nullable unwrap helpers must not re-wrap
				val member = expr(whenExpr.branches.last().result)
				core = """{"k":"cond","cond":{"k":"nullableHasValue","elem":${str(recvElem)},"e":$subj},"then":${present(member)},"else":{"k":"nullableNull","elem":${str(elem)}}}"""
			} else {
				valSubst[key] = subj
				val nullCheck = expr(whenExpr.branches.first().condition)
				val member = expr(whenExpr.branches.last().result)
				core = """{"k":"cond","cond":$nullCheck,"then":{"k":"nullableNull","elem":${str(elem)}},"else":${present(member)}}"""
			}
			restore()
			return if (subjVar == null) core
			else valueBlockJson(type = birType(block.type).toJson(), stmts = subjVar, result = core)
		}
		// `nv ?: d` where nv is a Nullable<T> -> evaluate once, then HasValue ? Value : d.
		if (origin == "ELVIS") nullableElem(tmp.type)?.let { elem ->
			val nv = "__nv${scopeCounter++}"
			val init = expr(tmp.initializer!!)
			// ELVIS lowers to `when { tmp == null -> fallback; else -> tmp }`:
			// branches[0].result is the fallback; branches.last() is tmp (ignored — we read .Value).
			val elseResult = expr(whenExpr.branches.first().result)
			val nvLoc = """{"k":"local","name":${str(nv)}}"""
			return valueBlockJson(
				type = null,
				stmts = """{"k":"var","name":${str(nv)},"type":${TypeNode.Nullable(elem).toJson()},"init":$init}""",
				result = """{"k":"cond","cond":{"k":"nullableHasValue","elem":${elem.toJson()},"e":$nvLoc},"then":{"k":"nullableValue","elem":${elem.toJson()},"e":$nvLoc},"else":$elseResult}""",
			)
		}
		val (subjVar, subj) = bindOnce(tmp.initializer!!, tmp.type, "__subj")
		valSubst[key] = subj
		val result = ternary(whenExpr)
		restore()
		// The wrapping valueBlock carries the when's result type: the old bare `cond` surfaced it (ternary's
		// "type"), and bir2cir's call-arg type inference sniffs the argument node's type field.
		return if (subjVar == null) result
		else valueBlockJson(type = birType(whenExpr.type).toJson(), stmts = subjVar, result = result)
	}
	// A general block in value position: emit its preceding (side-effecting) statements, then the last value.
	// e.g. `{ counter++ }` lowers to `{ val <unary> = counter; counter = counter + 1; <unary> }` — dropping the
	// leading statements would lose the temp + the assignment.
	val last = block.statements.lastOrNull()
	if (block.statements.size > 1 && last is IrExpression) {
		val pre = block.statements.dropLast(1).joinToString(",") { stmt(it) }
		return valueBlockJson(type = null, stmts = pre, result = expr(last))
	}
	return (last as? IrExpression)?.let { expr(it) } ?: """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}"""
}

internal fun BirEmitter.ternary(node: IrWhen): String {
	// Fold right-to-left into nested conditionals. The branches carry the when's result type, so a value-type
	// nullable result (`Int?`) gets its `T`/`null` branches coerced to Nullable<T> at emit (see EmitCond).
	// GOTCHA: an inlined `takeIf` etc. yields `if (c) x else null` whose joined type is a value primitive with
	// a bare `null` branch — but the emitted cond type comes out non-nullable (`kotlin.Int`). Two shapes reach
	// here: (1) the FIR `.type` is the non-null `Int` (the `T?` rides the fn return), or (2) `takeIf`'s generic
	// `T?` result, where `birType` substitutes `T -> kotlin.Int` and DROPS the `?`. Both are the same FRONTEND
	// FACT — a non-nullable join type with a `null`-yielding branch — recorded here as `joinNullBranch` on every
	// level of the emitted chain; bir2cir's ValueJoinNullWidening decides whether the physical slot has to widen
	// (it does for a VALUE join, where the alternative is a null reference stored over an `int`; a reference join
	// keeps its type, since a reference holds null already). The verdict is taken on the EMITTED result, because
	// that is where a nested join's own verdict and this emitter's own `valueBlock` wrapping are visible —
	// [emittedYieldsNull] states exactly which emitted shapes count. Each branch result is emitted exactly once.
	val branches = node.branches.map { b -> Triple((b.condition as? IrConst)?.value == true, b.condition, expr(b.result)) }
	val nullBranch = branches.any { emittedYieldsNull(it.third) }
	val bt = birType(node.type)
	val ty = bt.toJson()
	val joinFact = if (nullBranch && bt !is TypeNode.Nullable) """"joinNullBranch":true,""" else ""
	var acc = """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}"""
	for ((isElse, cond, result) in branches.asReversed()) {
		acc = if (isElse) result
		else """{"k":"cond",$joinFact"type":$ty,"cond":${expr(cond)},"then":$result,"else":$acc}"""
	}
	return acc
}

/** True iff an emitted branch VALUE may leave its join null — the compositional half of `joinNullBranch`.
 *
 *  A bare `null` const is the leaf case. The other case is a node that already decided it is one and says so on its
 *  ROOT: a NESTED JOIN (`if (a) (if (b) v else null) else w` shows the outer join a `cond` for its `then`, never a
 *  null const, and if only the inner widened, the inner's `Nullable<V>` would flow into a bare `V` join), or a
 *  `valueBlock` — the emitter's one PASS-THROUGH wrapper, which yields whatever its `result` yields and which every
 *  minting site stamps accordingly ([valueBlockJson]). Both stamp the fact as the FIRST key after `k` precisely so
 *  this test can be a ROOT test rather than a search — a `joinNullBranch` buried anywhere deeper belongs to a join
 *  that is not this branch's value and must not arm anything here.
 *
 *  THE RULE FOR WHAT ELSE IS TRANSPARENT, and it is short: nothing. The widening this arms only ever matters at a
 *  VALUE join, and at a value join the branch's static type IS that value type — so the only emitted forms that can
 *  leave a null in the slot are the ones carrying no value of that type at all (a `null` const, whose Kotlin type is
 *  `Nothing?`) and the pass-through of such a form. Every other wrapper the emitter can put around a `null` PRODUCES
 *  A VALUE of the wrapped-to type instead: a `cast` (`x as T`, IMPLICIT_CAST) asserts the branch has the join's own
 *  type, so at a value join it unboxes to a real value or throws — `if (c) 1 else (null as Int)` throws
 *  NullReferenceException, it does not answer null; `nullableValue` unwraps to a bare `V`, and
 *  `safeCastValue`/`nullableWrap` build a `Nullable<V>` struct, never a null reference; `isInstRef` can answer null
 *  but only at a REFERENCE join, which never widens. Seeing through those would over-stamp the fact and widen a join
 *  that never holds a null. */
internal fun BirEmitter.emittedYieldsNull(emitted: String): Boolean =
	isEmittedNullConst(emitted) ||
		emitted.startsWith("""{"k":"cond","joinNullBranch":true""") ||
		emitted.startsWith("""{"k":"valueBlock","joinNullBranch":true""")

/** The statement form of [emittedYieldsNull]: a branch whose result type is `Unit`/`Nothing` is emitted as a plain
 *  expression statement rather than an assignment, and a bare `null` literal (typed `Nothing?`) is exactly that. */
internal fun BirEmitter.emittedStmtYieldsNull(emitted: String): Boolean {
	val prefix = """{"k":"exprStmt","expr":"""
	return emitted.startsWith(prefix) && emitted.endsWith("}") &&
		emittedYieldsNull(emitted.substring(prefix.length, emitted.length - 1))
}

/** True if an EMITTED BIR expression is a bare `null` const — `{"k":"const",…,"value":null}` (a
 *  `void`/`kotlin.Nothing`-typed null). Used to spot a `when`/`if` branch that yields `null`. */
internal fun BirEmitter.isEmittedNullConst(emitted: String): Boolean =
	emitted.startsWith("""{"k":"const",""") && emitted.endsWith(""","value":null}""")
