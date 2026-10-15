package kotc.backend

import kotc.bir.TypeNode
import org.jetbrains.kotlin.ir.declarations.IrParameterKind
import org.jetbrains.kotlin.ir.declarations.IrSimpleFunction
import org.jetbrains.kotlin.ir.expressions.IrFunctionReference

internal fun BirEmitter.localFunctionBoundArguments(node: IrFunctionReference, fn: IrSimpleFunction) =
	localFunctionOwnParams(fn).mapNotNull { parameter ->
		node.arguments.getOrNull(fn.parameters.indexOf(parameter))?.let { parameter to it }
	}

/** Declaration argument order shared by local declarations and their forwarding references. This is Kotlin
 * vocabulary: lexical captures precede the declaration's dispatch/extension receivers, contexts and regulars. */
internal fun BirEmitter.localFunctionOwnParams(fn: IrSimpleFunction) =
	fn.parameters.filter { it.kind == IrParameterKind.DispatchReceiver } +
		listOfNotNull(extensionReceiverParam(fn)) + fn.parameters.filter { isValueParameter(it) }

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
	val typeArgs = if (local.typeParams.isEmpty()) "" else ""","typeArgs":[${
		localFunctionTypeArgs(local, fn, node.typeArguments).joinToString(",") { it.toJson() }
	}]"""
	if (local.captures.isEmpty() && bound.isEmpty())
		return """{"k":"localFunRef","id":${str(local.id)},"funcType":${funcType.toJson()}$typeArgs}"""

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
		"""{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":${str(name)}}"""
	val fields = capPairs.map { (decl, name) ->
		"""{"name":${str(name)},"type":${captureFieldType(decl).toJson()}}"""
	} + bound.zip(boundNames).map { (binding, name) ->
		"""{"name":${str(name)},"type":${birType(binding.first.type).toJson()}}"""
	}
	val callArgs = capPairs.map { (_, name) -> field(name) } + ownParams.map { parameter ->
		boundFields[parameter]?.let(::field)
			?: """{"k":"local","name":${str(arguments.getValue(parameter))}}"""
	}
	val sig = capPairs.map { (decl, _) -> captureFieldType(decl).toJson() } +
		ownParams.map { birType(it.type).toJson() }
	val call = """{"k":"callLocal","id":${str(local.id)},"sig":[${sig.joinToString(",")}],"args":[${callArgs.joinToString(",")}]$typeArgs}"""
	val body = if (funcType.ret == TypeNode.Fqn("kotlin.Unit")) """{"k":"exprStmt","expr":$call}"""
		else """{"k":"return","value":$call}"""
	val params = physicalTypes.zip(paramNames).joinToString(",") { (type, name) ->
		"""{"name":${str(name)},"type":${type.toJson()}}"""
	}
	// Keep the same lexical generic vector as the local declaration; bir2cir rebinds the synthetic payload to its
	// chosen closure owner. kotc supplies no CLR owner, method shape or delegate ABI.
	val synthClass = """{"name":${str(cname)},"fields":[${fields.joinToString(",")}],"params":[$params],"ret":${funcType.ret.toJson()},"body":[$body]${typeParamsJson(local.typeParams)}${semanticUseSiteOwnerJson()}}"""
	val values = local.captures.map { capValueExpr(it) } + bound.map { expr(it.second) }
	return """{"k":"newClosure","closureType":${fqnJson(cname)},"captures":[${values.joinToString(",")}],"method":"invoke","funcType":${funcType.toJson()}$typeArgs,"synthClass":$synthClass}"""
}
