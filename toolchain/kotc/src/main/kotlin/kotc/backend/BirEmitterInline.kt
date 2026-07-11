package kotc.backend

import kotc.bir.TypeNode
import org.jetbrains.kotlin.backend.common.collectTailRecursionCalls
import org.jetbrains.kotlin.descriptors.ClassKind
import org.jetbrains.kotlin.descriptors.Modality
import org.jetbrains.kotlin.descriptors.Visibilities
import org.jetbrains.kotlin.ir.declarations.IrDeclarationWithVisibility
import org.jetbrains.kotlin.ir.declarations.IrAnonymousInitializer
import org.jetbrains.kotlin.ir.declarations.IrClass
import org.jetbrains.kotlin.ir.declarations.IrFunction
import org.jetbrains.kotlin.ir.declarations.IrConstructor
import org.jetbrains.kotlin.ir.declarations.IrFile
import org.jetbrains.kotlin.ir.declarations.IrParameterKind
import org.jetbrains.kotlin.ir.declarations.IrProperty
import org.jetbrains.kotlin.ir.declarations.IrSimpleFunction
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
import org.jetbrains.kotlin.ir.expressions.IrStringConcatenation
import org.jetbrains.kotlin.ir.expressions.IrThrow
import org.jetbrains.kotlin.ir.expressions.IrTry
import org.jetbrains.kotlin.ir.expressions.IrWhen
import org.jetbrains.kotlin.ir.expressions.IrComposite
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
import org.jetbrains.kotlin.ir.util.classId
import org.jetbrains.kotlin.name.CallableId
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import org.jetbrains.kotlin.ir.util.resolveFakeOverride
import org.jetbrains.kotlin.ir.declarations.IrTypeParameter
import org.jetbrains.kotlin.ir.symbols.IrReturnTargetSymbol
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

/** AXIS ① (#75 finding 5): TRUE iff [call] passes a lambda literal to a FUNCTION-TYPED regular param. Gating on the
 *  PARAM's declared type being a function type (`birType(p.type) is TypeNode.Fn`) — the SAME predicate
 *  `isInlineWithLambda` (BirEmitterDeclarations) uses to stash the `[KotlinInline]` payload — keeps the splice trigger
 *  and the payload-travel predicate CONSISTENT: a call splices ⟺ the callee is stashed. A lambda passed to a GENERIC/
 *  `Any` param (`inline fun <T> hold(x: T); hold({ 1 })`) is NOT function-typed, so the callee is never stashed → it
 *  must fall through to the plain call, NOT attempt a splice (which would fail-loud on the missing payload — a crash on
 *  legal Kotlin). Index-aligned `parameters[i] ↔ arguments[i]` — NOT `regularArgs.zip` (regularArgs drops null defaulted
 *  slots, misaligning param↔arg). AXIS ① is modifier-blind (noinline/crossinline are AXIS ② in the emitters). */
internal fun BirEmitter.hasLambdaArg(call: IrCall): Boolean {
	val ps = call.symbol.owner.parameters
	return call.arguments.withIndex().any { (i, a) ->
		val p = ps.getOrNull(i) ?: return@any false
		p.kind == IrParameterKind.Regular && a is IrFunctionExpression && birType(p.type) is TypeNode.Fn
	}
}

/** Statements of a function/lambda body (block body, or a single-expression `= expr` body). */
internal fun BirEmitter.bodyStatements(body: org.jetbrains.kotlin.ir.IrElement?): List<org.jetbrains.kotlin.ir.IrStatement> = when (body) {
	is IrBlockBody -> body.statements
	is IrExpressionBody -> listOf(body.expression)
	else -> emptyList()
}

/** SAME-MODULE inline (#75): a call to a user/stdlib-self-build `inline fun` (body present in THIS run) taking ANY
 *  lambda arg (AXIS ①). Retires mechanism-1 (the old `inlineCall` splicer): instead of splicing the body HERE, kotc
 *  emits the SAME generic `callInline` node the cross-module emitters do (`inlineSpliceCall`/
 *  `inlineSpliceCallOwnerless`) and bir2cir's InlineSplice owns the splice — resolving the raw-BIR body from
 *  `InlineBirStash.Index` (keyed `owner|name|pc|ga` -> candidate list, disambiguated by structural `paramSig` match,
 *  spanning ALL files of this run). The ONLY difference from the owner-less emitter is that kotc CAN name the owner from
 *  its OWN naming: the enclosing type name for a member inline fun (`typeName`), else the top-level fun's file-facade
 *  class (`fileClassOf` — a cross-FILE same-module call works, the stash spans all files). A member fn's DISPATCH
 *  receiver rides `recvs.dispatch` (the payload's `{k:this}` refs bind to it, §4.3); an extension receiver rides
 *  `recvs.extension` (the leading `__self` payload param). One `args` entry per REGULAR param: a normal/crossinline
 *  literal lambda -> `emitInlineLambdaCarrier` (splice carrier), a NOINLINE lambda / any other arg -> its `expr` (a real
 *  delegate value) — AXIS ②. NO fallback slot: the engine fails loud if it cannot splice. A lambda-less inline call
 *  never reaches here — it falls through to the ordinary member-call path (a real emitted generic method; the JIT
 *  inlines it). */
internal fun BirEmitter.inlineSpliceCallSameModule(call: IrCall): String {
	val callee = call.symbol.owner
	val name = callee.name.asString()
	val extParam = callee.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
	val extRecv = extensionReceiver(call)
	val dispatchArg = dispatchReceiver(call)
	// A MEMBER extension inline fn (`class C { inline fun T.f(block) }`) has BOTH a dispatch AND an extension
	// receiver, but the stash's `recv` classification is single-valued and `extensionParam` shadows `dispatch`
	// (InlineBirStash), so the engine would bind only the extension receiver and the payload's dispatch `{k:this}`
	// refs (C's members) would rebind to the CALLER's `this` — a silent miscompile. FAIL LOUD rather than degrade.
	// Mirrors the facadegen gate's `extRecv != null` refusal. A member-EXTENSION inline fn with a lambda arg is not in
	// the stdlib.
	if (extParam != null && dispatchArg != null) return unsupported(call,
		"a same-module member-extension inline call with a lambda",
		"the member-extension dispatch+extension receiver splice shape is not yet supported")
	val params = callee.parameters.filter { it.kind == IrParameterKind.Regular }
	// One arg per Regular param, INDEX-ALIGNED with `params` (an omitted-default slot stays null): `regularArgs` drops
	// omitted-default nulls, which — now that ANY inline+lambda call splices (AXIS ①) — would shift the lambda into the
	// wrong param slot (a wrong AXIS-② noinline read / a carrier in the wrong slot). A null here lands in the CORRECT
	// param slot so bir2cir can attribute it to the right param; today bir2cir InlineSplice FAILS LOUD on a leftover
	// null (filling it from the payload param's carried default is a bir2cir follow-up — DefaultArgSplice today rewrites
	// only callStatic/callInstance, never callInline).
	val args: List<IrExpression?> = callee.parameters.withIndex()
		.filter { it.value.kind == IrParameterKind.Regular }
		.map { call.arguments.getOrNull(it.index) }
	// owner = kotc's OWN name for the hosting .NET type: the enclosing type name for a member inline fun, else the
	// top-level fun's file-facade class. Matches the stash key (a member is stashed under its type's `name`, a
	// top-level fun under the file's `fileClass` = `fileClassName`, which `fileClassOf` reproduces cross-file).
	val owner = (callee.parent as? IrClass)?.let { typeName(it) } ?: fileClassOf(callee)
	// pc = emitted param count (extension receiver counted as a leading `__self`); ga = type-param arity. The
	// `owner|name|pc|ga` key selects a candidate LIST; bir2cir picks the unique one whose `params[i].type` structurally
	// equals `paramSig[i]` (see `paramSigOf`).
	val pc = params.size + (if (extParam != null) 1 else 0)
	val ga = callee.typeParameters.size
	val typeArgs = callee.typeParameters.indices.joinToString(",") { i ->
		(call.typeArguments.getOrNull(i)?.let { birType(it) } ?: OBJ).toJson()
	}
	// Receivers: an extension receiver -> `recvs.extension` (payload param[0] == `__self`); a member dispatch receiver
	// -> `recvs.dispatch` (the payload's own `{k:this}` refs bind to it, §4.3). Both are carried when present.
	val recvParts = ArrayList<String>()
	extRecv?.let { recvParts.add(""""extension":${expr(it)}""") }
	dispatchArg?.let { recvParts.add(""""dispatch":${expr(it)}""") }
	// F2A (#75 finding 2A): carry the dispatch receiver's CONCRETIZED class type args as `recvs.dispatchTypeArgs` so
	// bir2cir can substitute the payload's `tv{scope:type,i}` (the i-th type param of the callee's OWNING generic class)
	// with the i-th carried arg — exactly as it substitutes `tv{scope:method,i}` from the node's `typeArgs`. Without
	// this, a CROSS-class same-module generic-owner member inline splice (this emitter has no same-class restriction)
	// leaves `tv{scope:type}` unbound and ilemit's ResolveTv silently falls back to `object` — a value-type miscompile.
	// REUSE `birType` (not raw `IrSimpleType.arguments`) so the args are BYTE-CONSISTENT with every other type on the
	// wire: star -> `kotlin.Any`, nullable markers preserved, and the FLATTENED enclosing+own type-param order that
	// `tvOf`'s scope:type index (`innerEnclosingTypeParams(owner).size + param.index`) expects.
	// Guards (fail -> OMIT the key, leaving the existing corpus byte-identical + the status-quo positional bind):
	//  - the receiver's static class must BE the callee's owning class (a `Derived : Base<Int>` receiver carries
	//    Derived's args, not Base's — supertype substitution is a documented follow-up, not F2A);
	//  - a nullable receiver type unwraps to its Fqn core; a type-parameter-typed receiver renders as `tv` (not Fqn) and
	//    correctly omits;
	//  - the rendered arg count must equal the owner's flattened type-param arity — a raw/star-projected USER generic
	//    receiver renders with NO args (birType falls through argument-less), and a short array would misalign the
	//    positional scope:type substitution.
	dispatchArg?.let { d ->
		val ownerClass = callee.parent as? IrClass
		if (ownerClass != null && d.type.classifierOrNull?.owner === ownerClass) {
			val core = birType(d.type).let { if (it is TypeNode.Nullable) it.of else it }
			val dta = (core as? TypeNode.Fqn)?.args
			val arity = innerEnclosingTypeParams(ownerClass).size + ownerClass.typeParameters.size
			if (dta != null && dta.size == arity && arity > 0)
				recvParts.add(""""dispatchTypeArgs":[${dta.joinToString(",") { it.toJson() }}]""")
		}
	}
	val recvs = "{${recvParts.joinToString(",")}}"
	// One entry per REGULAR param, in order: a literal lambda -> an `inlineLambda` carrier; any other arg -> its expr.
	val argsJson = params.indices.joinToString(",") { i ->
		val arg = args.getOrNull(i)
		// AXIS ②: a NOINLINE lambda arg is a REAL delegate value (emit via `expr` -> newDelegate/newClosure), NOT a
		// splice carrier -> inside the spliced body its `param()` becomes a delegate INVOKE on the bound temp. A normal
		// or CROSSINLINE lambda rides as an `inlineLambda` carrier bir2cir splices at its invoke sites. `params[i]` and
		// `args[i]` are the i-th Regular param/arg (index-aligned above), so the noinline flag is read off the RIGHT param.
		if (arg is IrFunctionExpression && !params[i].isNoinline) emitInlineLambdaCarrier(arg)
		else if (arg != null) expr(arg)
		else "null"
	}
	val retType = birType(callee.returnType).toJson()
	return """{"k":"callInline","callee":${str(callee.fqNameWhenAvailable?.asString() ?: name)},"owner":${str(owner)},"pc":$pc,"ga":$ga,"typeArgs":[$typeArgs],"recvs":$recvs,"args":[$argsJson],"retType":$retType,"paramSig":[${paramSigOf(callee)}]}"""
}

/** CROSS-MODULE inline: a call to a facadegen-injected `inline fun` taking ANY lambda arg (AXIS ①; its `[KotlinInline]`
 *  body lives on the referenced assembly). kotc emits a GENERIC `callInline` node carrying the call bindings — the type
 *  args, one entry per regular param (a normal/crossinline literal lambda as an `inlineLambda` carrier, a NOINLINE
 *  lambda / any other arg as its `expr` = a real delegate — AXIS ②). bir2cir OWNS the splice: it re-lowers the carried
 *  body in the app context (so it binds against app types). There is NO `fallback` slot — the engine fails loud if it
 *  cannot splice. This facadegen path is gated at the call site on `extRecv == null` (its receiver shape is untested — a
 *  receiver call takes the owner-less path instead), so `recvs` is always empty here. */
internal fun BirEmitter.inlineSpliceCall(call: IrCall, fileClass: String): String {
	val callee = call.symbol.owner
	val name = callee.name.asString()
	val extRecv = extensionReceiver(call)   // null here — the call-site gate guarantees it
	val params = callee.parameters.filter { it.kind == IrParameterKind.Regular }
	// One arg per Regular param, INDEX-ALIGNED with `params` (an omitted-default slot stays null): `regularArgs` drops
	// omitted-default nulls, which — now that ANY inline+lambda call splices (AXIS ①) — would shift the lambda into the
	// wrong param slot (a wrong AXIS-② noinline read / a carrier in the wrong slot). A null here lands in the CORRECT
	// param slot so bir2cir can attribute it to the right param; today bir2cir InlineSplice FAILS LOUD on a leftover
	// null (filling it from the payload param's carried default is a bir2cir follow-up — DefaultArgSplice today rewrites
	// only callStatic/callInstance, never callInline).
	val args: List<IrExpression?> = callee.parameters.withIndex()
		.filter { it.value.kind == IrParameterKind.Regular }
		.map { call.arguments.getOrNull(it.index) }
	// Disambiguate the file-facade overload (forEach/count/... exist for Iterable/Array/CharSequence): the .NET method's
	// param count = regular params + the receiver-as-__self, and its generic arity = the fn's type params. SAME as the
	// retired inlineSplice node's pc/ga.
	val pc = params.size + (if (extRecv != null) 1 else 0)
	val ga = callee.typeParameters.size
	// One type-arg entry per callee type param (a null/star projection -> object; bir2cir's SubstTv resolves it).
	val typeArgs = callee.typeParameters.indices.joinToString(",") { i ->
		(call.typeArguments.getOrNull(i)?.let { birType(it) } ?: OBJ).toJson()
	}
	// One entry per REGULAR param, in order: a literal lambda -> an `inlineLambda` carrier; any other arg -> its expr.
	val argsJson = params.indices.joinToString(",") { i ->
		val arg = args.getOrNull(i)
		// AXIS ②: a NOINLINE lambda arg is a REAL delegate value (emit via `expr` -> newDelegate/newClosure), NOT a
		// splice carrier -> inside the spliced body its `param()` becomes a delegate INVOKE on the bound temp. A normal
		// or CROSSINLINE lambda rides as an `inlineLambda` carrier bir2cir splices at its invoke sites. `params[i]` and
		// `args[i]` are the i-th Regular param/arg (index-aligned above), so the noinline flag is read off the RIGHT param.
		if (arg is IrFunctionExpression && !params[i].isNoinline) emitInlineLambdaCarrier(arg)
		else if (arg != null) expr(arg)
		else "null"
	}
	val retType = birType(callee.returnType).toJson()
	return """{"k":"callInline","callee":${str(callee.fqNameWhenAvailable?.asString() ?: name)},"owner":${str(fileClass)},"pc":$pc,"ga":$ga,"typeArgs":[$typeArgs],"recvs":{},"args":[$argsJson],"retType":$retType,"paramSig":[${paramSigOf(callee)}]}"""
}

/** paramSig (#95 §4.2, overload disambiguator): one TYPE NODE per callee DECLARED parameter in the SAME order the
 *  DECLARATION emits `params` — the extension receiver as element 0 (the leading `__self`), then the Regular+Context
 *  params. bir2cir keys `InlineBirStash.Index` by `owner|name|pc|ga` (a candidate LIST) and picks the unique candidate
 *  whose `params[i].type` DeepEquals this `paramSig[i]`. recv0 (first-param FQN) was insufficient — Kotlin inline
 *  overloads differ at ANY param position (Duration.toComponents ×4 by lambda arity; flatMap/maxOf by the lambda's
 *  return type). CRITICAL: each type is emitted in the callee's OWN type-parameter frame — `typeArgSubst` is BYPASSED
 *  so a param `(T)->R` serializes as `{t:fn,params:[{t:tv,scope:method,i:0}],ret:{t:tv,scope:method,i:1}}` IDENTICALLY
 *  here and at the decl site (never instantiated to the call's type args). */
internal fun BirEmitter.paramSigOf(callee: IrSimpleFunction): String {
	val extParam = callee.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
	val sigParams = buildList {
		extParam?.let { add(it) }
		callee.parameters.filter { isValueParameter(it) }.forEach { add(it) }
	}
	// Bypass any ambient type-arg substitution so the callee's declared param types render in their un-instantiated
	// frame (matching the DECLARATION's `params[i].type`, which InlineBirStash reads).
	val saved = HashMap(typeArgSubst)
	typeArgSubst.clear()
	return try {
		sigParams.joinToString(",") { birType(it.type).toJson() }
	} finally {
		typeArgSubst.clear(); typeArgSubst.putAll(saved)
	}
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
	// SHADOW the lambda's own regular params in `valSubst` while emitting its body: an enclosing lambda carrier
	// may have bound the SAME name (e.g. `it`) to an outer local. Without removing it here, the body's ref to this
	// lambda's param would resolve to the OUTER binding — the carrier param is named correctly but the body dangles
	// on a foreign local. Emitting them as BARE `{"k":"local","name":<param>}` refs lets bir2cir bind the carrier
	// param. (The ext-receiver already does this via `selfSubst`.) Saved + restored around the body emission.
	val shadowed = regularParams.map { it.name.asString() }.associateWith { valSubst[it] }
	shadowed.keys.forEach { valSubst.remove(it) }
	val body = ArrayList<String>()
	// The `selfSubst[extParam]` binding above (restored just below) is the guarantee that the receiver's `this`/
	// implicit-member refs resolve to `freshRecv`, not a dangling `{"k":"this"}` — no post-hoc string guard needed.
	val result = spliceBodyWithReturns(fn, fn.returnType.isUnit(), body)
	shadowed.forEach { (name, prev) -> if (prev != null) valSubst[name] = prev else valSubst.remove(name) }
	if (extParam != null) { if (hadSelf) selfSubst[extParam] = savedSelf!! else selfSubst.remove(extParam) }
	// CAPTURES (bir2cir §4.4ii MaterializeCarrier): the free vars the carrier body references, computed by REUSING
	// kotc's real-closure capture machinery (`capturedVars`/`captureFieldType`) so the set + types EXACTLY equal what a
	// real closure over this same lambda would capture. Each entry's `name` + `outer`-ness is BYTE-CONSISTENT with how
	// the SAME value is referenced in the carrier's already-emitted `body`/`result`, so bir2cir's rewrite lands:
	//  - an enclosing EXTENSION receiver goes through `selfSubst` -> the body references it as `{k:local,name:<n>}` (a
	//    top-level ext fn's `__self`, or a nested receiver-lambda's fresh recv), so it is a REGULAR local capture with
	//    that SAME name (NO `outer`) — bir2cir field-ifies it via the normal `{k:local,name:X}`->`this.X` path.
	//  - a genuine DISPATCH receiver is NOT in `selfSubst` -> the body references it as a bare `{k:this}`, so it is the
	//    `{name:"__outer",outer:true}` capture (bir2cir rewrites `{k:this}`->`this.__outer`). Its `type` is
	//    `birType(enclosingClass.type)` = the enclosing class WITH its own type args (e.g. DeepRecursiveScopeImpl<T,R> as
	//    fqn args:[tv T, tv R]), so bir2cir types the `__outer` field correctly on a generic enclosing class.
	//  - any other captured local/param -> {name:<var>,type} (its own name, matching the bare `{k:local}` body ref).
	// A member EXTENSION inline fn capturing BOTH emits two entries (one `__self` regular, one `__outer`). The lambda's
	// OWN params/ext-receiver are `declared` (excluded). Emitted on EVERY carrier (cheap); bir2cir consumes it only when
	// it must materialize the carrier into a closure (the common invoke-and-splice path ignores it).
	val capturesJson = capturedVars(fn, includeThis = true).joinToString(",") { d ->
		val selfRef = selfSubst[d]
		when {
			// Enclosing extension receiver: name it EXACTLY as the body's `selfSubst` local ref (`{"k":"local","name":<n>}`).
			selfRef != null -> {
				val nm = selfRef.substringAfter(""""name":"""").substringBefore('"')
				"""{"name":${str(nm)},"type":${captureFieldType(d).toJson()}}"""
			}
			d.name.asString() == "<this>" ->
				"""{"name":"__outer","type":${captureFieldType(d).toJson()},"outer":true}"""
			else ->
				"""{"name":${str(captureFieldName(d))},"type":${captureFieldType(d).toJson()}}"""
		}
	}
	return """{"k":"inlineLambda","params":[$paramsJson],"captures":[$capturesJson],"body":[${body.joinToString(",")}],"result":$result}"""
}

/** CROSS-MODULE inline of ANY klib stdlib inline+lambda fn taking ANY lambda arg (AXIS ①) — the scope/util fns
 *  (let/run/with/apply/also/use), collection ops (forEach/map/filter), takeIf/takeUnless, etc. — whose `[KotlinInline]`
 *  raw-BIR body lives on the ref.dll. Unlike `inlineSpliceCall`, kotc CANNOT name the hosting file class — the whole
 *  stdlib rides the klib, facadegen supplies no `kotlin.*` metadata — so the node is OWNER-LESS: bir2cir resolves the
 *  hosting file class from the ref.dll `[KotlinInline]` index keyed `name|pc|ga` (a candidate list, disambiguated by
 *  structural `paramSig` match). An extension receiver rides in `recvs.extension`; `with`'s receiver is a REGULAR param
 *  and rides as a plain arg. Each regular arg splits per AXIS ②: a normal/crossinline literal lambda -> an `inlineLambda`
 *  carrier, a NOINLINE lambda / any other arg -> its `expr` (a real delegate). There is NO `fallback` slot — the engine
 *  fails loud if it cannot splice. */
internal fun BirEmitter.inlineSpliceCallOwnerless(call: IrCall, extRecv: IrExpression?): String {
	val callee = call.symbol.owner
	val name = callee.name.asString()
	val params = callee.parameters.filter { it.kind == IrParameterKind.Regular }
	// One arg per Regular param, INDEX-ALIGNED with `params` (an omitted-default slot stays null): `regularArgs` drops
	// omitted-default nulls, which — now that ANY inline+lambda call splices (AXIS ①) — would shift the lambda into the
	// wrong param slot (a wrong AXIS-② noinline read / a carrier in the wrong slot). A null here lands in the CORRECT
	// param slot so bir2cir can attribute it to the right param; today bir2cir InlineSplice FAILS LOUD on a leftover
	// null (filling it from the payload param's carried default is a bir2cir follow-up — DefaultArgSplice today rewrites
	// only callStatic/callInstance, never callInline).
	val args: List<IrExpression?> = callee.parameters.withIndex()
		.filter { it.value.kind == IrParameterKind.Regular }
		.map { call.arguments.getOrNull(it.index) }
	// The .NET method param count = regular params + the receiver-as-__self; generic arity = the fn's type params.
	// Matches the ref.dll payload key owner|name|pc|ga.
	val pc = params.size + (if (extRecv != null) 1 else 0)
	val ga = callee.typeParameters.size
	val typeArgs = callee.typeParameters.indices.joinToString(",") { i ->
		(call.typeArguments.getOrNull(i)?.let { birType(it) } ?: OBJ).toJson()
	}
	val extRecvJson = extRecv?.let { expr(it) }
	val recvs = if (extRecvJson != null) """{"extension":$extRecvJson}""" else "{}"
	// One entry per REGULAR param, in order: a literal lambda -> an `inlineLambda` carrier; any other arg -> its expr
	// (for `with`, the receiver is regular param[0] and rides as a plain expr; the lambda is param[1]).
	val argsJson = params.indices.joinToString(",") { i ->
		val arg = args.getOrNull(i)
		// AXIS ②: a NOINLINE lambda arg is a REAL delegate value (emit via `expr` -> newDelegate/newClosure), NOT a
		// splice carrier -> inside the spliced body its `param()` becomes a delegate INVOKE on the bound temp. A normal
		// or CROSSINLINE lambda rides as an `inlineLambda` carrier bir2cir splices at its invoke sites. `params[i]` and
		// `args[i]` are the i-th Regular param/arg (index-aligned above), so the noinline flag is read off the RIGHT param.
		if (arg is IrFunctionExpression && !params[i].isNoinline) emitInlineLambdaCarrier(arg)
		else if (arg != null) expr(arg)
		else "null"
	}
	val retType = birType(callee.returnType).toJson()
	// The §4.2 overload disambiguator (see `paramSigOf`): one TYPE NODE per callee declared param (extension receiver as
	// element 0), in the callee's OWN un-instantiated frame. bir2cir keys the ref.dll payload by owner(null)|name|pc|ga
	// (a candidate list) and picks the candidate whose `params[i].type` DeepEquals this `paramSig[i]`.
	return """{"k":"callInline","callee":${str(callee.fqNameWhenAvailable?.asString() ?: name)},"owner":null,"pc":$pc,"ga":$ga,"typeArgs":[$typeArgs],"recvs":$recvs,"args":[$argsJson],"retType":$retType,"paramSig":[${paramSigOf(callee)}]}"""
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

/** The call-site splice gate (#75 inline unification).
 *  AXIS ① — does the FUNCTION splice? TRUE iff the callee is `inline` and is passed ANY function-typed argument. The
 *  param's `noinline`/`crossinline` modifier is IRRELEVANT to THIS decision — that is AXIS ②, decided PER-ARG in the
 *  emitters (a `noinline` arg → a REAL delegate value; a normal or `crossinline` arg → a spliceable carrier). So even a
 *  `noinline`-only inline fn splices (its body is spliced with the noinline lambda bound to a delegate temp; `block()`
 *  inside becomes a delegate invoke). A lambda-less inline call is a plain call (the JIT inlines the small method). No
 *  escape analysis: splicing handles every semantic case (non-local return / break / captured-`val` write / inherited
 *  suspension) correctly by construction. Essentially `callee.isInline && hasLambdaArg(call)`, EXCEPT the two coroutine
 *  suspension intrinsics are carved out (`isSuspendCoroutineIntrinsic`) — they must stay a plain call, not a splice.
 *  `hasLambdaArg` gates the lambda arg on the param being FUNCTION-TYPED — the SAME check `isInlineWithLambda` uses to
 *  stash the payload — so this trigger and the stash predicate are CONSISTENT: a call splices ⟺ the callee is stashed. */
internal fun BirEmitter.callNeedsSplice(call: IrCall): Boolean {
	val callee = call.symbol.owner
	return callee.isInline && hasLambdaArg(call) && !isSuspendCoroutineIntrinsic(callee)
}

/** The two coroutine SUSPENSION intrinsics (`suspendCoroutine`, `suspendCoroutineUninterceptedOrReturn`) that must NOT
 *  be source-inlined despite being inline+lambda. `suspendCoroutineUninterceptedOrReturn`'s body is the FAKE
 *  `throw NotImplementedError("…is intrinsic")` — splicing it would replace the suspension with a throw — and BOTH are
 *  the suspension POINT that bir2cir's SuspendColdLowering reconstructs from the PLAIN `callStatic <name>(<delegate>)
 *  suspendCall:true` shape (its F2 recognizer keys on that by FQN, `owner` null-or-stdlib, arg[0] a newClosure/
 *  newDelegate). So they fall through to the ordinary call path with the block materialized as a real delegate — kotc
 *  does NOT splice them, in ANY build (the crossinline exclusion that used to protect them is gone under #75's
 *  splice-all). A Kotlin-language coroutine identity (like kotc's other name-recognized intrinsics), NOT CLR knowledge. */
internal fun BirEmitter.isSuspendCoroutineIntrinsic(callee: IrSimpleFunction): Boolean =
	callee.fqNameWhenAvailable?.asString().let {
		it == "kotlin.coroutines.suspendCoroutine" ||
			it == "kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn"
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
