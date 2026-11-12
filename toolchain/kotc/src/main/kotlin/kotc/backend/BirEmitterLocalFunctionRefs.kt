package kotc.backend

import kotc.bir.TypeNode
import org.jetbrains.kotlin.ir.declarations.IrParameterKind
import org.jetbrains.kotlin.ir.declarations.IrSimpleFunction
import org.jetbrains.kotlin.ir.declarations.IrTypeParameter
import org.jetbrains.kotlin.ir.declarations.IrValueParameter
import org.jetbrains.kotlin.ir.expressions.IrFunctionReference
import org.jetbrains.kotlin.ir.types.IrType
import org.jetbrains.kotlin.ir.types.IrTypeSubstitutor
import org.jetbrains.kotlin.ir.types.defaultType

internal fun BirEmitter.localFunctionBoundArguments(node: IrFunctionReference, fn: IrSimpleFunction) =
	localFunctionOwnParams(fn).mapNotNull { parameter ->
		node.arguments.getOrNull(fn.parameters.indexOf(parameter))?.let { parameter to it }
	}

/** Declaration argument order shared by local declarations and their forwarding references. This is Kotlin
 * vocabulary: lexical captures precede the declaration's dispatch/extension receivers, contexts and regulars. */
internal fun BirEmitter.localFunctionOwnParams(fn: IrSimpleFunction) =
	fn.parameters.filter { it.kind == IrParameterKind.DispatchReceiver } +
		listOfNotNull(extensionReceiverParam(fn)) + fn.parameters.filter { isValueParameter(it) }

internal data class LocalFunctionReferenceFrame(
	val parameterTypes: Map<IrValueParameter, IrType>,
	val typeParams: List<IrTypeParameter>,
)

/** Close the target's own Kotlin type parameters at the reference site before describing an adapter's values.
 * Its free frame belongs to the use site: `U := List<T>` captures T, not the declaration's unrelated U. */
internal fun BirEmitter.localFunctionReferenceFrame(
	node: IrFunctionReference,
	fn: IrSimpleFunction,
	local: BirEmitter.LocalFunctionFact,
): LocalFunctionReferenceFrame {
	val actuals = fn.typeParameters.indices.map { checkNotNull(node.typeArguments.getOrNull(it)) }
	val substitution = IrTypeSubstitutor(fn.typeParameters.map { it.symbol }, actuals, true)
	val params = localFunctionOwnParams(fn).associateWith { substitution.substitute(it.type) }
	val lexical = local.typeParams.filter { it !in fn.typeParameters }.map { it.symbol.defaultType }
	val free = freeTypeParams(local.captures.map { it.type } + params.values +
		listOf(node.type) + actuals + lexical)
	return LocalFunctionReferenceFrame(params, free)
}

/** A local reference binds the target's lexical captures and the receiver values explicitly supplied by FIR.
 * Remaining arguments come from the reference's resolved callable type, not the target's unbound signature.
 * Receiver values are evaluated once at reference creation, independently of the mutable captured cells. */
internal fun BirEmitter.localFunctionRef(
	node: IrFunctionReference,
	fn: IrSimpleFunction,
	local: BirEmitter.LocalFunctionFact,
	funcType: TypeNode.Fn,
): String {
	val ownParams = localFunctionOwnParams(fn)
	val bound = localFunctionBoundArguments(node, fn)
	val resolvedTypeArgs = if (local.typeParams.isEmpty()) null else localFunctionTypeArgs(local, fn, node.typeArguments)
	if (local.captures.isEmpty() && bound.isEmpty())
		return kotc.bir.BirLocalFunctionReference(local.id, funcType, resolvedTypeArgs).toJson()
	val callTypeArgs = resolvedTypeArgs?.joinToString(",", ",\"typeArgs\":[", "]") { it.toJson() }.orEmpty()

	val frame = localFunctionReferenceFrame(node, fn, local)
	val closureTypeArgs = liftedTypeArgsJson(frame.typeParams)
	val cname = "dotkt\$${synthScope}\$Closure${closureCounter++}"
	val boundNames = bound.indices.map { "dotkt\$bound$it" }
	val capPairs = uniqueCaptureNames(local.captures, boundNames.toMutableSet())
	val boundFields = bound.zip(boundNames).associate { (binding, name) -> binding.first to name }
	val physicalTypes = listOfNotNull(funcType.recv) + funcType.params
	val remaining = ownParams.filter { it !in boundFields }
	check(remaining.size == physicalTypes.size) { "local callable reference lost its frontend argument binding" }
	val paramNames = physicalTypes.indices.map { "dotkt\$arg$it" }
	val arguments = remaining.zip(paramNames).toMap()
	fun field(name: String) =
		kotc.bir.BirFieldGet(ownerType = TypeNode.Fqn(cname), recv = kotc.bir.BirThis(), name = name)
	val fields = capPairs.map { (decl, name) ->
		"""{"name":${str(name)},"type":${captureFieldType(decl).toJson()}}"""
	} + bound.zip(boundNames).map { (binding, name) ->
		"""{"name":${str(name)},"type":${birType(frame.parameterTypes.getValue(binding.first)).toJson()}}"""
	}
	val callArgs = capPairs.map { (_, name) -> field(name) } + ownParams.map { parameter ->
		boundFields[parameter]?.let(::field)
			?: kotc.bir.BirLocal(arguments.getValue(parameter)).toJson()
	}
	val sig = capPairs.map { (decl, _) -> captureFieldType(decl).toJson() } +
		ownParams.map { birType(it.type).toJson() }
	val call = """{"k":"callLocal","id":${str(local.id)},"sig":[${sig.joinToString(",")}],"args":[${callArgs.joinToString(",")}]$callTypeArgs}"""
	val body = if (funcType.ret == TypeNode.Fqn("kotlin.Unit")) kotc.bir.BirExpressionStatement(expr = call)
		else """{"k":"return","value":$call}"""
	val params = physicalTypes.zip(paramNames).joinToString(",") { (type, name) ->
		"""{"name":${str(name)},"type":${type.toJson()}}"""
	}
	// The adapter's values and the call's type arguments now share the same enclosing Kotlin frame. bir2cir alone
	// chooses the closure owner and rebinds those free parameters into its physical declaration.
	val synthClass = """{"name":${str(cname)},"fields":[${fields.joinToString(",")}],"params":[$params],"ret":${funcType.ret.toJson()},"body":[$body]${typeParamsJson(frame.typeParams)}${semanticUseSiteOwnerJson()}}"""
	val values = local.captures.map { capValueExpr(it) } + bound.map { expr(it.second) }
	return """{"k":"newClosure","closureType":${fqnJson(cname)},"captures":[${values.joinToString(",")}],"method":"invoke","funcType":${funcType.toJson()}$closureTypeArgs,"synthClass":$synthClass}"""
}
