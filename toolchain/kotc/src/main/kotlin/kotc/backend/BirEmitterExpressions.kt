package kotc.backend

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
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import org.jetbrains.kotlin.ir.util.resolveFakeOverride
import org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI
import org.jetbrains.kotlin.ir.types.IrType
import org.jetbrains.kotlin.ir.types.classFqName
import org.jetbrains.kotlin.ir.types.classifierOrNull
import org.jetbrains.kotlin.ir.types.isUnit
import org.jetbrains.kotlin.ir.types.isMarkedNullable
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import org.jetbrains.kotlin.ir.IrFileEntry
import org.jetbrains.kotlin.cli.common.messages.MessageCollector
import org.jetbrains.kotlin.cli.common.messages.CompilerMessageSeverity
import org.jetbrains.kotlin.cli.common.messages.CompilerMessageLocation
import java.io.File

// BIR expression rendering: an IrExpression -> a BIR JSON node (extension on BirEmitter).
internal fun BirEmitter.expr(node: IrExpression): String {
	// Spilled suspension: a nested `.await()` already hoisted into a state-machine field by spillExpr; the
	// residual expression references that field instead of re-evaluating the suspension. (await spilling, D)
	coSpill[node]?.let { return """{"k":"local","name":${str(it)}}""" }
	return exprInner(node)
}

internal fun BirEmitter.exprInner(node: IrExpression): String = when (node) {
	is IrConst -> """{"k":"const","type":${str(birType(node.type))},"value":${constJson(node)}}"""
	is IrGetValue -> {
		val owner = node.symbol.owner
		val name = owner.name.asString()
		when {
			// A ref-cell var read `x` -> `x.v` (the heap cell, reached via the capture field inside a closure).
			isRefCell(owner) -> """{"k":"field","ownerType":${str(refTypeName(owner))},"recv":${refBase(owner)},"name":"v"}"""
			captureSubst.containsKey(owner) -> captureSubst[owner]!!
			selfSubst.containsKey(owner) -> selfSubst[owner]!!   // extension `__self` (by identity, before name-based `<this>`)
			valSubst.containsKey(name) -> valSubst[name]!!
			name == "<this>" -> """{"k":"this"}"""
			else -> {
				// Smart-cast narrowing carried directly on the IrGetValue (no IMPLICIT_CAST node — e.g. the `&&`
				// RHS / a compound condition: `x is Int && x > 10`): the use-site type is narrower than the
				// declared type, so emit a cast (ilemit unboxes Any->Int / castclass for refs). Without it the
				// value keeps its boxed/declared form and ops like `>` compare the wrong thing.
				val ut = birType(node.type); val dt = birType(owner.type)
				// The declared type is the boxed Any token ("object" fallback, or "kotlin.Any" for an Any/Nothing source type).
				if (ut != dt && (dt == "object" || dt == "kotlin.Any")) """{"k":"cast","type":${str(ut)},"e":{"k":"local","name":${str(name)}}}"""
				else """{"k":"local","name":${str(name)}}"""
			}
		}
	}
	is IrGetEnumValue -> {
		val entry = node.symbol.owner
		val parent = entry.parent as? IrClass
		// Rich enum -> the static singleton field; basic enum -> ordinal const typed as the CLR enum.
		if (parent != null && isRichEnum(parent))
			"""{"k":"staticField","ownerType":${str(typeName(parent))},"name":${str(entry.name.asString())}}"""
		else {
			val ord = parent?.declarations?.filterIsInstance<IrEnumEntry>()?.indexOf(entry) ?: 0
			"""{"k":"enumValue","type":${str("@" + parent?.let { typeName(it) })},"ordinal":$ord}"""
		}
	}
	// `object Foo` reference -> load the singleton `Foo.INSTANCE` static field (item 10). (.NET-injected objects
	// like Math are static call sites handled at the call site; only user singletons reach here as a value.)
	is IrGetObjectValue ->
		when (node.symbol.owner.fqNameWhenAvailable?.asString()) {
			// The `Unit` object as a VALUE (e.g. `Result.success(Unit)`) -> the DotKt.Unit singleton; under
			// stdlib-compile the stdlib's OWN kotlin.Unit object (its INSTANCE) is used (no DotKt.Runtime).
			"kotlin.Unit" -> if (!stdlibCompile) """{"k":"clrStaticField","type":"DotKt.Unit","name":"Instance"}"""
				else """{"k":"staticField","ownerType":${str(typeName(node.symbol.owner))},"name":"INSTANCE"}"""
			else -> """{"k":"staticField","ownerType":${str(typeName(node.symbol.owner))},"name":"INSTANCE"}"""
		}
	is IrBlock -> blockExpr(node)
	is IrGetField -> {
		val ownerClass = node.symbol.owner.parent as? IrClass
		val clr = ownerClass?.let { clrName(it) }
		val recvJson = node.receiver?.let { expr(it) } ?: """{"k":"this"}"""
		val fldName = node.symbol.owner.name.asString()
		val ownerFq = ownerClass?.fqNameWhenAvailable?.asString()
		val recvFq = node.receiver?.type?.classFqName?.asString()
		val isThrowableProp = (fldName == "message" || fldName == "cause") &&
			(ownerFq == "kotlin.Throwable" || ownerClass?.name?.asString() == "Throwable" || recvFq == "kotlin.Throwable"
				|| isThrowableType(node.receiver?.type))
		// `Throwable.message`/`.cause` -> System.Exception.Message/.InnerException. A .NET member (e.g. inherited
		// `Exception.Message`) is modeled as a field by the FIR injector but is really a property getter call.
		if (isThrowableProp) {
			val (prop, rt) = if (fldName == "message") "Message" to "System.String" else "InnerException" to "System.Exception"
			"""{"k":"clrPropGet","type":"System.Exception","name":${str(prop)},"retType":${str(rt)},"static":false,"recv":$recvJson}"""
		} else if (!stdlibCompile && (ownerFq == "kotlin.Result" || recvFq == "kotlin.Result")) {
			// kotlin.Result is an inline value class -> isSuccess/isFailure/value/failure arrive as IrGetField.
			// Map onto the shared DotKt.Result<T> struct properties (see T4 / docs §13n).
			val spec = node.receiver?.type?.let { birType(it) } ?: "clrg:DotKt.Result[object]"
			val (prop, rt) = when (fldName) {
				"isSuccess" -> "IsSuccess" to "bool"
				"isFailure" -> "IsFailure" to "bool"
				"failure" -> "ExceptionOrNull" to "clr:System.Exception"
				else -> "Value" to netType(node.type)
			}
			"""{"k":"clrPropGet","type":${str(spec)},"name":${str(prop)},"retType":${str(rt)},"static":false,"recv":$recvJson}"""
		} else if (clr != null)
			"""{"k":"clrPropGet","type":${str(clr)},"name":${str(fldName)},"retType":${str(netType(node.type))},"static":false,"recv":$recvJson}"""
		// A `lateinit var` backing-field read -> throw if still uninitialized (null) — proper lateinit semantics.
		else if (node.symbol.owner.correspondingPropertySymbol?.owner?.isLateinit == true)
			"""{"k":"lateinitGet","ownerType":${str(ownerSpec(ownerClass, node.receiver?.type))},"recv":$recvJson,"name":${str(fldName)}}"""
		else
			"""{"k":"field","ownerType":${str(ownerSpec(ownerClass, node.receiver?.type))},"recv":$recvJson,"name":${str(fldName)}}"""
	}
	is IrConstructorCall -> {
		val klass = node.symbol.owner.parent as? IrClass
		// `IntArray(size){init}` / `IntArray(size)` -> a real BCL array (newarr + fill loop), NOT a kotlin.IntArray object.
		// The GENERIC object array `Array<E>(size){init}` is the same intrinsic: the element is the CLR type of E
		// (`Array<Any?>` -> object[]), so a concrete E works (a bare type-param E rides its `gp:E` form). Without this it
		// fell through to a bogus `new kotlin.Array(...)` (wrong-sized array).
		val arrElem = ARRAY_CLASS_ELEM[klass?.fqNameWhenAvailable?.asString()]
			?: if (klass?.fqNameWhenAvailable?.asString() == "kotlin.Array") {
				val elemType = (((node.type as? IrSimpleType)?.arguments?.firstOrNull()) as? IrTypeProjection)?.type
				// Only a CONCRETE element type routes to a real BCL newarr (`Array<Any?>` -> object[]). A bare TYPE-PARAM
				// element (`Array<T>`) needs reified allocation; routing it here would newarr a `gp:T` AND make its init
				// `Func<int,T>` a TypeBuilderInstantiation (ilemit GetMethod("Invoke") fails) -> leave it to the fall-through.
				if (elemType != null && elemType.classifierOrNull !is org.jetbrains.kotlin.ir.symbols.IrTypeParameterSymbol) birType(elemType) else null
			} else null
		val arrArgs = if (arrElem != null) filledArgExprs(node) else emptyList()
		if (arrElem != null && arrArgs.size == 2)
			"""{"k":"newArrayInit","elem":${str(arrElem)},"size":${expr(arrArgs[0])},"init":${expr(arrArgs[1])}}"""
		else if (arrElem != null && arrArgs.size == 1)
			"""{"k":"newArraySized","elem":${str(arrElem)},"size":${expr(arrArgs[0])}}"""
		else {
		// A generic .NET type (`Collection<Int>()`) -> a constructed `clrg:` spec; non-generic stays plain.
		val clr = klass?.let { clrName(it) }?.let { net ->
			val args = (node.type as? IrSimpleType)?.arguments?.mapNotNull { (it as? IrTypeProjection)?.type?.let(::birType) }
			if (args.isNullOrEmpty()) net else "clrg:$net[${args.joinToString(",")}]"
		}
		// Kotlin builtin exceptions (IllegalStateException etc.) -> their .NET counterpart.
		val netExc = klass?.fqNameWhenAvailable?.asString()?.let { NET_EXCEPTIONS[it] }
		// A collection ctor `ArrayList<R>()` / `HashSet<T>()` (kotlin.collections.* = java.util.* typealiases) -> the
		// BCL collection (`new List<R>()` / `new HashSet<T>()`): birType already maps the type. Lets the real stdlib
		// `map`/`filter`/`mapTo` (which build an ArrayList) compile straight to the BCL collection DotKt uses.
		val mapped = clr ?: netExc
		if (mapped != null)
			"""{"k":"clrNew","type":${str(mapped)},"argTypes":[${paramNetTypes(node.symbol.owner)}],"args":[${filledArgExprs(node).joinToString(",") { expr(it) }}]}"""
		else {
			// An inner-class ctor takes the enclosing instance (its dispatch receiver) as a leading arg.
			val outerArg = if (klass?.isInner == true) dispatchReceiver(node)?.let { expr(it) } else null
			// A lifted local class prepends its captured outer locals (evaluated here, in the outer context).
			val capArgs = klass?.let { localClassCaptures[it] }?.map { capValueExpr(it) } ?: emptyList()
			val args = (listOfNotNull(outerArg) + capArgs + filledArgExprs(node).map { expr(it) }).joinToString(",")
			"""{"k":"new","type":${str(klass?.let { ownerSpec(it, node.type) } ?: "object")},"args":[$args]}"""
		}
		}
	}
	is IrStringConcatenation -> """{"k":"concat","parts":[${node.arguments.joinToString(",") { expr(it) }}]}"""
	is IrTypeOperatorCall -> when (node.operator) {
		// `x is T` (exhaustive when matching) -> isinst + not-null check.
		IrTypeOperator.INSTANCEOF -> """{"k":"isinst","type":${str(birType(node.typeOperand))},"e":${expr(node.argument)}}"""
		IrTypeOperator.NOT_INSTANCEOF -> """{"k":"un","op":"!","e":{"k":"isinst","type":${str(birType(node.typeOperand))},"e":${expr(node.argument)}}}"""
		// `x as T` / smart-cast downcast -> castclass (or unbox for value types). Throws on mismatch.
		IrTypeOperator.CAST, IrTypeOperator.IMPLICIT_CAST ->
			"""{"k":"cast","type":${str(birType(node.typeOperand))},"e":${expr(node.argument)}}"""
		// `x as? T` -> null on mismatch. Reference T: `isinst T` (null or ref). Value T: `T?` (Nullable<T>).
		IrTypeOperator.SAFE_CAST -> {
			val velem = VALUE_PRIM_BIR[node.typeOperand.classFqName?.asString()]
			if (velem != null) """{"k":"safeCastValue","elem":${str(velem)},"e":${expr(node.argument)}}"""
			else """{"k":"isinstRef","type":${str(birType(node.typeOperand))},"e":${expr(node.argument)}}"""
		}
		// A fun-interface SAM conversion (`Comparator { a, b -> … }`) -> a synthetic class implementing the interface
		// (the SAM method = the lambda body), NOT a Func delegate -- a delegate has no `compare` so a call site that
		// uses the value by interface (`comparator.compare(...)`) throws EntryPointNotFound. See samConversion.
		IrTypeOperator.SAM_CONVERSION -> samConversion(node)
		// Coercions to Unit / not-null pass the value through.
		else -> expr(node.argument)
	}
	is IrWhen -> ternary(node)
	// `try { … } catch { … }` in VALUE position (`val x = try …`, `return try …`, a try in a lambda) -> a temp
	// local assigned in each branch, wrapped in a valueBlock (a CLR try/catch leaves no value on the stack).
	is IrTry -> tryExpr(node)
	// `T::class` / `Foo::class` -> a System.Type token. For a generic param `T` this is `ldtoken !!0` in the
	// generic method (CLR reified generics); `Foo::class` is a concrete `ldtoken Foo`.
	is IrClassReference -> """{"k":"classRef","type":${str(birType(node.classType))}}"""
	// `x::class` (runtime class of an instance) -> `x.GetType()` (a System.Type); `.simpleName`/`.qualifiedName`
	// on the result route to Type.Name/FullName, same as the `T::class` literal path.
	is IrGetClass -> """{"k":"getType","e":${expr(node.argument)}}"""
	// `throw` in expression position (e.g. `x ?: throw ...`, `if (c) v else throw ...`): type Nothing,
	// transfers control so no value reaches the surrounding merge point.
	is IrThrow -> throwExpr(expr(node.value))
	// `return` used in expression position (`val x = if (c) a else return`; `x ?: return -1`). Like throwExpr,
	// it transfers control so no value reaches the surrounding merge.
	is IrReturn -> if (node.value.type.isUnit()) """{"k":"returnExpr"}""" else """{"k":"returnExpr","value":${expr(node.value)}}"""
	is IrCall -> call(node)
	// A property reference passed to a delegate's getValue/setValue -> a `new KPropertyImpl("<name>")`.
	is IrPropertyReference -> {
		needsKProperty = true
		"""{"k":"new","type":"<>dotkt_KPropertyImpl","args":[{"k":"const","type":"string","value":${str(node.symbol.owner.name.asString())}}]}"""
	}
	is IrFunctionExpression -> lambda(node)
	// A callable reference `::foo` -> a delegate bound to the referenced function (same Func/Action as a lambda).
	is IrFunctionReference -> functionRef(node)
	// A `vararg` argument -> a newArray. A spread `*a` (IrSpreadElement) passes an existing array: a lone
	// spread is forwarded as-is; all-literal builds a fresh array; mixed `f(1,*a,2)` is a clean deferral.
	is IrVararg -> {
		val spreads = node.elements.filterIsInstance<IrSpreadElement>()
		val directs = node.elements.filterIsInstance<IrExpression>()
		when {
			spreads.size == 1 && directs.isEmpty() -> expr(spreads[0].expression)
			spreads.isEmpty() -> """{"k":"newArray","elem":${str(birType(node.varargElementType))},"elems":[${directs.joinToString(",") { expr(it) }}]}"""
			// `f(1, *a, 2)` -> build a List<elem> (Add literals / AddRange spreads), then ToArray.
			else -> {
				val parts = node.elements.joinToString(",") { e ->
					when (e) {
						is IrSpreadElement -> """{"spread":true,"e":${expr(e.expression)}}"""
						is IrExpression -> """{"spread":false,"e":${expr(e)}}"""
						else -> """{"spread":false,"e":{"k":"const","type":"void","value":null}}"""
					}
				}
				"""{"k":"spreadConcat","elem":${str(birType(node.varargElementType))},"parts":[$parts]}"""
			}
		}
	}
	else -> unsupported(node, "this expression", "the IR node ${node::class.simpleName} has no .NET lowering")
}
