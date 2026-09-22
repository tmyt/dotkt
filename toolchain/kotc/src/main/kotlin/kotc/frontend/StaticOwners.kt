package kotc.frontend

import org.jetbrains.kotlin.fir.types.ConeKotlinType
import org.jetbrains.kotlin.ir.types.IrType

/** The constructed Kotlin declaration owner selected before Fir2Ir discards a static qualifier. */
object ClrStaticOwners {
	private data class Key(val file: String, val end: Int, val name: String, val kind: String)
	private val source = mutableMapOf<Key, ConeKotlinType>()
	private val ir = mutableMapOf<Key, IrType>()
	fun reset() { source.clear(); ir.clear() }
	fun record(file: String, end: Int, name: String, kind: String, owner: ConeKotlinType) {
		val key = Key(file, end, name, kind)
		val previous = source.putIfAbsent(key, owner)
		check(previous == null || previous == owner) { "Conflicting static owner facts at $key" }
	}
	fun recordAssignment(file: String, propertyEnd: Int, assignmentEnd: Int, name: String) {
		source[Key(file, propertyEnd, name, "set")]?.let { record(file, assignmentEnd, name, "set", it) }
	}
	fun convertTypes(convert: (ConeKotlinType) -> IrType) {
		for ((key, owner) in source) ir[key] = convert(owner)
	}
	fun at(file: String?, end: Int, name: String, kind: String): IrType? =
		file?.let { ir[Key(it, end, name, kind)] }
}
