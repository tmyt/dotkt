package clrc.backend

import org.jetbrains.kotlin.ir.declarations.IrFile
import org.jetbrains.kotlin.ir.declarations.IrParameterKind
import org.jetbrains.kotlin.ir.declarations.IrSimpleFunction
import org.jetbrains.kotlin.ir.declarations.IrVariable
import org.jetbrains.kotlin.ir.expressions.IrBlock
import org.jetbrains.kotlin.ir.expressions.IrBlockBody
import org.jetbrains.kotlin.ir.expressions.IrCall
import org.jetbrains.kotlin.ir.expressions.IrConst
import org.jetbrains.kotlin.ir.expressions.IrExpression
import org.jetbrains.kotlin.ir.expressions.IrGetValue
import org.jetbrains.kotlin.ir.expressions.IrReturn
import org.jetbrains.kotlin.ir.expressions.IrSetValue
import org.jetbrains.kotlin.ir.expressions.IrStringConcatenation
import org.jetbrains.kotlin.ir.expressions.IrWhen
import org.jetbrains.kotlin.ir.expressions.IrWhileLoop
import org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI
import org.jetbrains.kotlin.ir.types.IrType
import org.jetbrains.kotlin.ir.types.classFqName
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

	fun emitFile(file: IrFile): String {
		val functions = file.declarations.filterIsInstance<IrSimpleFunction>()
		if (functions.isEmpty()) return ""
		val className = File(file.fileEntry.name).name.removeSuffix(".kt")
			.replaceFirstChar { it.uppercaseChar() } + "Kt"
		val hasMain = functions.any { it.name.asString() == "main" && it.parameters.none { p -> p.kind == IrParameterKind.Regular } }
		val methods = functions.joinToString(",") { method(it) }
		return """{"fileClass":${str(className)},"hasMain":$hasMain,"methods":[$methods]}"""
	}

	private fun method(fn: IrSimpleFunction): String {
		val params = fn.parameters
			.filter { it.kind == IrParameterKind.Regular }
			.joinToString(",") { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" }
		val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
		return """{"name":${str(fn.name.asString())},"static":true,"params":[$params],"ret":${str(birType(fn.returnType))},"body":[$body]}"""
	}

	private fun stmt(node: org.jetbrains.kotlin.ir.IrElement): String = when (node) {
		is IrVariable -> """{"k":"var","name":${str(node.name.asString())},"type":${str(birType(node.type))},"init":${node.initializer?.let { expr(it) } ?: "null"}}"""
		is IrSetValue -> """{"k":"setLocal","name":${str(node.symbol.owner.name.asString())},"value":${expr(node.value)}}"""
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
		is IrGetValue -> """{"k":"local","name":${str(node.symbol.owner.name.asString())}}"""
		is IrStringConcatenation -> """{"k":"concat","parts":[${node.arguments.joinToString(",") { expr(it) }}]}"""
		is IrWhen -> ternary(node)
		is IrCall -> call(node)
		else -> """{"k":"unsupportedExpr","of":${str(node::class.simpleName ?: "?")}}"""
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
		val args = call.arguments.filterNotNull()
		BINARY[name]?.let { if (args.size == 2) return """{"k":"bin","op":${str(it)},"l":${expr(args[0])},"r":${expr(args[1])}}""" }
		UNARY[name]?.let { if (args.size == 1) return """{"k":"un","op":${str(it)},"e":${expr(args[0])}}""" }
		val fq = (callee.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)?.packageFqName?.asString()
		if (fq == "kotlin.io" && (name == "println" || name == "print")) {
			val m = if (name == "println") "WriteLine" else "Write"
			return """{"k":"console","method":${str(m)},"args":[${args.joinToString(",") { expr(it) }}]}"""
		}
		// Sibling top-level call.
		return """{"k":"callStatic","owner":null,"method":${str(name)},"args":[${args.joinToString(",") { expr(it) }}]}"""
	}

	private fun constJson(c: IrConst): String = when (val v = c.value) {
		is String -> str(v)
		is Boolean -> v.toString()
		is Char -> str(v.toString())
		null -> "null"
		else -> v.toString()
	}

	private fun birType(t: IrType): String = when (t.classFqName?.asString()) {
		"kotlin.Unit", "kotlin.Nothing" -> "void"
		"kotlin.Int" -> "int"
		"kotlin.Long" -> "long"
		"kotlin.Double" -> "double"
		"kotlin.Float" -> "float"
		"kotlin.Boolean" -> "bool"
		"kotlin.Char" -> "char"
		"kotlin.String" -> "string"
		else -> "object"
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
