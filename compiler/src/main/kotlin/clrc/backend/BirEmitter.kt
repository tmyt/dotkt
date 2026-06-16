package clrc.backend

import org.jetbrains.kotlin.descriptors.ClassKind
import org.jetbrains.kotlin.descriptors.Modality
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
import org.jetbrains.kotlin.ir.expressions.IrTypeOperatorCall
import org.jetbrains.kotlin.ir.expressions.IrWhen
import org.jetbrains.kotlin.ir.expressions.IrWhileLoop
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI
import org.jetbrains.kotlin.ir.types.IrType
import org.jetbrains.kotlin.ir.types.classFqName
import org.jetbrains.kotlin.ir.types.classifierOrNull
import org.jetbrains.kotlin.ir.types.isUnit
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import java.io.File

/**
 * D1.1 — Backend IR (BIR) emitter.
 *
 * Serializes the M0 subset of a file to a compact JSON the future `ilemit` tool consumes to emit
 * CIL directly (no C# in between). The IR walk mirrors [CSharpCodegen]; only the rendering target
 * differs (a structured AST as JSON instead of C# text). Stack lowering is deferred to ilemit.
 *
 * Scope (M0): top-level functions; const/local/binop/unop/call/concat/ternary; var/set/return/
 * while/if. Classes & interop are later milestones (D1.4+).
 */
@OptIn(UnsafeDuringIrConstructionAPI::class)
class BirEmitter {

	// Inline substitutions for synthetic temporaries (e.g. a `when` subject) in expression position.
	private val valSubst = HashMap<String, String>()

	fun emitFile(file: IrFile): String {
		val functions = file.declarations.filterIsInstance<IrSimpleFunction>()
		val classes = file.declarations.filterIsInstance<IrClass>().filter { it.kind == ClassKind.CLASS && clrName(it) == null }
		val interfaces = file.declarations.filterIsInstance<IrClass>().filter { it.kind == ClassKind.INTERFACE && clrName(it) == null }
		if (functions.isEmpty() && classes.isEmpty() && interfaces.isEmpty()) return ""
		val className = File(file.fileEntry.name).name.removeSuffix(".kt")
			.replaceFirstChar { it.uppercaseChar() } + "Kt"
		val hasMain = functions.any { it.name.asString() == "main" && it.parameters.none { p -> p.kind == IrParameterKind.Regular } }
		val methods = functions.joinToString(",") { method(it, static = true) }
		val types = (interfaces.map { interfaceDef(it) } + classes.map { typeDef(it) }).joinToString(",")
		return """{"fileClass":${str(className)},"hasMain":$hasMain,"methods":[$methods],"types":[$types]}"""
	}

	private fun interfaceDef(iface: IrClass): String {
		val methods = iface.declarations.filterIsInstance<IrSimpleFunction>()
			.filter { it.correspondingPropertySymbol == null && !it.isFakeOverride }
			.joinToString(",") {
				"""{"name":${str(it.name.asString())},"static":false,"override":false,"virtual":true,"params":[${paramsJson(it.parameters)}],"ret":${str(birType(it.returnType))},"body":[]}"""
			}
		return """{"name":${str(iface.name.asString())},"kind":"interface","base":null,"fields":[],"ctors":[],"methods":[$methods]}"""
	}

	private fun typeDef(klass: IrClass): String {
		val base = klass.superTypes.mapNotNull { it.classifierOrNull?.owner as? IrClass }
			.firstOrNull { it.kind == ClassKind.CLASS && it.fqNameWhenAvailable?.asString() != "kotlin.Any" }
		val fields = klass.declarations.filterIsInstance<IrProperty>().mapNotNull { it.backingField }
			.joinToString(",") { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" }
		val ctors = klass.declarations.filterIsInstance<IrConstructor>().joinToString(",") { ctor(klass, it) }
		val methods = klass.declarations.filterIsInstance<IrSimpleFunction>()
			.filter { it.correspondingPropertySymbol == null && !it.isFakeOverride && it.body != null }
			.joinToString(",") { method(it, static = false) }
		val baseJson = base?.let { str(it.name.asString()) } ?: "null"
		val ifaces = klass.superTypes.mapNotNull { it.classifierOrNull?.owner as? IrClass }
			.filter { it.kind == ClassKind.INTERFACE }
			.joinToString(",") { str(it.name.asString()) }
		return """{"name":${str(klass.name.asString())},"kind":"class","base":$baseJson,"interfaces":[$ifaces],"fields":[$fields],"ctors":[$ctors],"methods":[$methods]}"""
	}

	private fun ctor(klass: IrClass, ctor: IrConstructor): String {
		val params = paramsJson(ctor.parameters)
		val body = ctor.body as? IrBlockBody
		val delegating = body?.statements?.filterIsInstance<IrDelegatingConstructorCall>()?.firstOrNull()
		val baseArgs = delegating?.let { d ->
			val targetFq = (d.symbol.owner.parent as? IrClass)?.fqNameWhenAvailable?.asString()
			if (targetFq != "kotlin.Any") d.arguments.filterNotNull().joinToString(",") { expr(it) } else null
		}
		val stmts = ArrayList<String>()
		body?.statements?.forEach { s ->
			when (s) {
				is IrDelegatingConstructorCall -> {}
				is IrInstanceInitializerCall -> klass.declarations.forEach { d ->
					when (d) {
						is IrProperty -> d.backingField?.initializer?.let {
							stmts.add("""{"k":"setField","ownerType":${str(klass.name.asString())},"recv":{"k":"this"},"name":${str(d.name.asString())},"value":${expr((it as IrExpressionBody).expression)}}""")
						}
						is IrAnonymousInitializer -> (d.body as? IrBlockBody)?.statements?.forEach { stmts.add(stmt(it)) }
						else -> {}
					}
				}
				else -> stmts.add(stmt(s))
			}
		}
		val baseJson = baseArgs?.let { "[$it]" } ?: "null"
		return """{"params":[$params],"baseArgs":$baseJson,"body":[${stmts.joinToString(",")}]}"""
	}

	private fun method(fn: IrSimpleFunction, static: Boolean): String {
		val isOverride = fn.overriddenSymbols.any { (it.owner.parent as? IrClass)?.kind == ClassKind.CLASS }
		val isVirtual = fn.modality == Modality.OPEN || fn.modality == Modality.ABSTRACT
		val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
		return """{"name":${str(fn.name.asString())},"static":$static,"override":$isOverride,"virtual":$isVirtual,"params":[${paramsJson(fn.parameters)}],"ret":${str(birType(fn.returnType))},"body":[$body]}"""
	}

	private fun paramsJson(params: List<org.jetbrains.kotlin.ir.declarations.IrValueParameter>): String =
		params.filter { it.kind == IrParameterKind.Regular }
			.joinToString(",") { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" }

	private fun stmt(node: org.jetbrains.kotlin.ir.IrElement): String = when (node) {
		is IrVariable -> """{"k":"var","name":${str(node.name.asString())},"type":${str(birType(node.type))},"init":${node.initializer?.let { expr(it) } ?: "null"}}"""
		is IrSetValue -> """{"k":"setLocal","name":${str(node.symbol.owner.name.asString())},"value":${expr(node.value)}}"""
		is IrSetField -> """{"k":"setField","ownerType":${str((node.symbol.owner.parent as? IrClass)?.name?.asString() ?: "?")},"recv":${node.receiver?.let { expr(it) } ?: """{"k":"this"}"""},"name":${str(node.symbol.owner.name.asString())},"value":${expr(node.value)}}"""
		is IrReturn -> if (node.value.type.isUnit()) """{"k":"return"}""" else """{"k":"return","value":${expr(node.value)}}"""
		is IrWhileLoop -> """{"k":"while","cond":${expr(node.condition)},"body":[${(node.body as? IrBlock)?.statements?.joinToString(",") { stmt(it) } ?: ""}]}"""
		is IrWhen -> whenStmt(node)
		is IrBlock -> """{"k":"block","body":[${node.statements.joinToString(",") { stmt(it) }}]}"""
		is IrExpression -> """{"k":"exprStmt","expr":${expr(node)}}"""
		else -> """{"k":"unsupportedStmt","of":${str(node::class.simpleName ?: "?")}}"""
	}

	private fun whenStmt(node: IrWhen): String {
		val branches = node.branches.joinToString(",") {
			val isElse = (it.condition as? IrConst)?.value == true
			if (isElse) """{"else":true,"body":[${stmt(it.result)}]}"""
			else """{"cond":${expr(it.condition)},"body":[${stmt(it.result)}]}"""
		}
		return """{"k":"if","branches":[$branches]}"""
	}

	private fun expr(node: IrExpression): String = when (node) {
		is IrConst -> """{"k":"const","type":${str(birType(node.type))},"value":${constJson(node)}}"""
		is IrGetValue -> {
			val name = node.symbol.owner.name.asString()
			when {
				valSubst.containsKey(name) -> valSubst[name]!!
				name == "<this>" -> """{"k":"this"}"""
				else -> """{"k":"local","name":${str(name)}}"""
			}
		}
		is IrGetEnumValue -> {
			// Lower an enum entry to its ordinal (int); equality/when then compare ints.
			val entry = node.symbol.owner
			val entries = (entry.parent as? IrClass)?.declarations?.filterIsInstance<IrEnumEntry>().orEmpty()
			"""{"k":"const","type":"int","value":${entries.indexOf(entry)}}"""
		}
		is IrBlock -> blockExpr(node)
		is IrGetField -> """{"k":"field","ownerType":${str((node.symbol.owner.parent as? IrClass)?.name?.asString() ?: "?")},"recv":${node.receiver?.let { expr(it) } ?: """{"k":"this"}"""},"name":${str(node.symbol.owner.name.asString())}}"""
		is IrConstructorCall -> {
			val klass = node.symbol.owner.parent as? IrClass
			val clr = klass?.let { clrName(it) }
			if (clr != null)
				"""{"k":"clrNew","type":${str(clr)},"argTypes":[${paramNetTypes(node.symbol.owner)}],"args":[${regularArgs(node).joinToString(",") { expr(it) }}]}"""
			else
				"""{"k":"new","type":${str(klass?.name?.asString() ?: "object")},"args":[${regularArgs(node).joinToString(",") { expr(it) }}]}"""
		}
		is IrStringConcatenation -> """{"k":"concat","parts":[${node.arguments.joinToString(",") { expr(it) }}]}"""
		is IrTypeOperatorCall -> expr(node.argument) // coercions / implicit casts pass through
		is IrWhen -> ternary(node)
		is IrCall -> call(node)
		else -> """{"k":"unsupportedExpr","of":${str(node::class.simpleName ?: "?")}}"""
	}

	private fun regularArgs(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): List<IrExpression> {
		val params = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.parameters ?: emptyList()
		return call.arguments.mapIndexedNotNull { i, a ->
			if (a != null && i < params.size && params[i].kind == IrParameterKind.Regular) a else null
		}
	}

	private fun dispatchReceiver(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): IrExpression? {
		val params = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.parameters ?: return null
		val idx = params.indexOfFirst { it.kind == IrParameterKind.DispatchReceiver }
		return if (idx in 0 until call.arguments.size) call.arguments[idx] else null
	}

	private fun blockExpr(block: IrBlock): String {
		// `when (subject)` lowers to `{ val tmp = subject; WHEN }` in expression position.
		val tmp = block.statements.getOrNull(0) as? IrVariable
		val whenExpr = block.statements.getOrNull(1) as? IrWhen
		if (block.statements.size == 2 && tmp != null && whenExpr != null && tmp.initializer != null) {
			val key = tmp.name.asString()
			valSubst[key] = expr(tmp.initializer!!)
			val result = ternary(whenExpr)
			valSubst.remove(key)
			return result
		}
		return (block.statements.lastOrNull() as? IrExpression)?.let { expr(it) } ?: """{"k":"const","type":"void","value":null}"""
	}

	private fun ternary(node: IrWhen): String {
		// Fold right-to-left into nested conditionals.
		var acc = """{"k":"const","type":"void","value":null}"""
		for (b in node.branches.asReversed()) {
			val isElse = (b.condition as? IrConst)?.value == true
			acc = if (isElse) expr(b.result)
			else """{"k":"cond","cond":${expr(b.condition)},"then":${expr(b.result)},"else":$acc}"""
		}
		return acc
	}

	private fun call(call: IrCall): String {
		val callee = call.symbol.owner
		val name = callee.name.asString()
		val declaringClass = callee.parent as? IrClass
		val isBuiltin = declaringClass?.fqNameWhenAvailable?.asString()?.startsWith("kotlin") ?: true

		// BCL interop: a call whose declaring class is `@Clr("System.X")` resolves to a real .NET member.
		val clrType = declaringClass?.let { clrName(it) }
		if (clrType != null) {
			val recv = dispatchReceiver(call)
			val isStatic = recv == null || recv is IrGetObjectValue
			val prop = callee.correspondingPropertySymbol?.owner
			if (prop != null) {
				val pn = clrName(prop) ?: prop.name.asString()
				val recvJson = if (isStatic) "null" else expr(recv!!)
				return if (callee === prop.setter)
					"""{"k":"clrPropSet","type":${str(clrType)},"name":${str(pn)},"static":$isStatic,"recv":$recvJson,"value":${expr(regularArgs(call).first())}}"""
				else """{"k":"clrPropGet","type":${str(clrType)},"name":${str(pn)},"retType":${str(netType(callee.returnType))},"static":$isStatic,"recv":$recvJson}"""
			}
			val member = clrName(callee) ?: name
			val argsJson = regularArgs(call).joinToString(",") { expr(it) }
			val ret = str(netType(callee.returnType))
			return if (isStatic)
				"""{"k":"clrStatic","type":${str(clrType)},"method":${str(member)},"argTypes":[${paramNetTypes(callee)}],"ret":$ret,"args":[$argsJson]}"""
			else
				"""{"k":"clrInstance","type":${str(clrType)},"method":${str(member)},"argTypes":[${paramNetTypes(callee)}],"ret":$ret,"recv":${expr(recv!!)},"args":[$argsJson]}"""
		}

		// Property get/set on a user class -> field access.
		val property = callee.correspondingPropertySymbol?.owner
		if (property != null && declaringClass != null) {
			val recv = dispatchReceiver(call)?.let { expr(it) } ?: """{"k":"this"}"""
			val owner = str(declaringClass.name.asString())
			return if (callee === property.setter)
				"""{"k":"setFieldExpr","ownerType":$owner,"recv":$recv,"name":${str(property.name.asString())},"value":${expr(regularArgs(call).first())}}"""
			else """{"k":"field","ownerType":$owner,"recv":$recv,"name":${str(property.name.asString())}}"""
		}

		if (isBuiltin) {
			val operands = call.arguments.filterNotNull()
			BINARY[name]?.let { if (operands.size == 2) return """{"k":"bin","op":${str(it)},"l":${expr(operands[0])},"r":${expr(operands[1])}}""" }
			UNARY[name]?.let { if (operands.size == 1) return """{"k":"un","op":${str(it)},"e":${expr(operands[0])}}""" }
			val fq = (callee.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)?.packageFqName?.asString()
			if (fq == "kotlin.io" && (name == "println" || name == "print")) {
				val m = if (name == "println") "WriteLine" else "Write"
				return """{"k":"console","method":${str(m)},"args":[${operands.joinToString(",") { expr(it) }}]}"""
			}
		}

		val args = regularArgs(call).joinToString(",") { expr(it) }
		val recv = dispatchReceiver(call)
		// Instance method on a user class, or a sibling top-level call.
		return if (recv != null) {
			val owner = str(declaringClass?.name?.asString() ?: "?")
			val virtual = callee.modality != Modality.FINAL || callee.overriddenSymbols.isNotEmpty()
			"""{"k":"callInstance","ownerType":$owner,"virtual":$virtual,"recv":${expr(recv)},"method":${str(name)},"args":[$args]}"""
		} else """{"k":"callStatic","owner":null,"method":${str(name)},"args":[$args]}"""
	}

	/** Reads the .NET name from a `@Clr("...")` annotation, if present. */
	private fun clrName(decl: org.jetbrains.kotlin.ir.declarations.IrAnnotationContainer): String? {
		for (a in decl.annotations) {
			if ((a as? IrConstructorCall)?.type?.classFqName?.asString() == "clr.Clr")
				return (a.arguments.firstOrNull() as? IrConst)?.value as? String
		}
		return null
	}

	/** A type's fully-qualified .NET name, for IL reflection-based member resolution. */
	private fun netType(t: IrType): String = when (t.classFqName?.asString()) {
		"kotlin.Int" -> "System.Int32"
		"kotlin.Long" -> "System.Int64"
		"kotlin.Short" -> "System.Int16"
		"kotlin.Byte" -> "System.SByte"
		"kotlin.Double" -> "System.Double"
		"kotlin.Float" -> "System.Single"
		"kotlin.Boolean" -> "System.Boolean"
		"kotlin.Char" -> "System.Char"
		"kotlin.String" -> "System.String"
		"kotlin.Unit" -> "System.Void"
		else -> (t.classifierOrNull?.owner as? IrClass)?.let { clrName(it) } ?: "System.Object"
	}

	private fun paramNetTypes(callee: org.jetbrains.kotlin.ir.declarations.IrFunction): String =
		callee.parameters.filter { it.kind == IrParameterKind.Regular }
			.joinToString(",") { str(netType(it.type)) }

	private fun constJson(c: IrConst): String = when (val v = c.value) {
		is String -> str(v)
		is Boolean -> v.toString()
		is Char -> str(v.toString())
		null -> "null"
		else -> v.toString()
	}

	private fun birType(t: IrType): String {
		when (t.classFqName?.asString()) {
			"kotlin.Unit", "kotlin.Nothing" -> return "void"
			"kotlin.Int" -> return "int"
			"kotlin.Long" -> return "long"
			"kotlin.Double" -> return "double"
			"kotlin.Float" -> return "float"
			"kotlin.Boolean" -> return "bool"
			"kotlin.Char" -> return "char"
			"kotlin.String" -> return "string"
		}
		val klass = t.classifierOrNull?.owner as? IrClass
		// A @Clr façade type is a real .NET type ("clr:System.Text.StringBuilder").
		klass?.let { clrName(it) }?.let { return "clr:$it" }
		// Enums are lowered to their ordinal (int).
		if (klass != null && klass.kind == ClassKind.ENUM_CLASS) return "int"
		// A user-declared class becomes a reference to that BIR type ("@Name").
		if (klass != null && klass.kind == ClassKind.CLASS) return "@" + klass.name.asString()
		return "object"
	}

	private fun str(s: String): String =
		"\"" + s.replace("\\", "\\\\").replace("\"", "\\\"").replace("\n", "\\n").replace("\t", "\\t") + "\""

	companion object {
		private val BINARY = mapOf(
			"plus" to "+", "minus" to "-", "times" to "*", "div" to "/", "rem" to "%",
			"less" to "<", "lessOrEqual" to "<=", "greater" to ">", "greaterOrEqual" to ">=",
			"EQEQ" to "==", "EQEQEQ" to "==",
		)
		private val UNARY = mapOf("unaryMinus" to "-", "unaryPlus" to "+", "not" to "!")
	}
}
