package kotc.backend

import kotc.bir.*
import org.jetbrains.kotlin.ir.IrElement
import org.jetbrains.kotlin.ir.declarations.*
import org.jetbrains.kotlin.ir.types.*
import org.jetbrains.kotlin.ir.util.file
import org.jetbrains.kotlin.types.Variance

/** A declared T is a classifier, not a slot to substitute at an inline/default use site. */
internal fun BirEmitter.reflectedSourceType(type: IrType, site: IrElement): BirExpression {
    val visiting = mutableSetOf<IrTypeParameter>()
    fun variance(v: Variance) = when (v) {
        Variance.IN_VARIANCE -> "in"
        Variance.OUT_VARIANCE -> "out"
        Variance.INVARIANT -> "invariant"
    }
    fun declaredShape(t: IrType, bound: Boolean): BirExpression? {
        val parameter = t.classifierOrNull?.owner as? IrTypeParameter
        if (parameter != null && (bound || !parameter.isReified)) {
            if (!visiting.add(parameter)) return unsupported(site, "typeOf", "recursive type parameter bound")
            val parent = parameter.parent
            val owner: String
            val id: String
            when (parent) {
                is IrClass -> { owner = typeName(parent); id = owner }
                is IrFunction -> {
                    owner = semanticOwnerName(parent)
                    val local = (parent as? IrSimpleFunction)?.let(localFunctionIds::get)
                    id = if (local != null) "local:${fileClassName(parent.file)}:$local" else declarationId(parent)
                }
                else -> error("Unexpected Kotlin type parameter owner: $parent")
            }
            val bounds = parameter.superTypes.map { declaredShape(it, true) ?: BirReflectedType(birType(it)) }
            visiting.remove(parameter)
            return BirReflectedParameter(TypeNode.Fqn(owner), id, parameter.index, parameter.name.asString(),
                variance(parameter.variance), parameter.isReified, t.isMarkedNullable(), bounds)
        }
        val arguments = (t as? IrSimpleType)?.arguments.orEmpty()
        if (arguments.isEmpty()) return null
        val shapes = arguments.map { arg -> (arg as? IrTypeProjection)?.let { declaredShape(it.type, bound) } }
        if (shapes.all { it == null }) return null
        return BirReflectedType(birType(t), arguments.mapIndexed { i, arg ->
            if (arg is IrTypeProjection) BirReflectedProjection(variance(arg.variance), shapes[i] ?: BirReflectedType(birType(arg.type)))
            else BirReflectedProjection("star")
        })
    }
    return declaredShape(type, false) ?: BirReflectedType(birType(type))
}
