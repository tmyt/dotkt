// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.
package kotc.bir

data class TypeParameterDeclaration(
    val name: String,
    val reified: Boolean? = null,
    val variance: String? = null,
    val constraints: List<TypeNode>? = null
) {
    fun toJson(): String = buildString {
        append('{')
        append("\"name\":")
        TypeNodeJson.writeString(name, this)
        reified?.let { child ->
            append(",\"reified\":")
            append(child)
        }
        variance?.let { child ->
            append(",\"variance\":")
            TypeNodeJson.writeString(child, this)
        }
        constraints?.let { child ->
            append(",\"constraints\":")
            append('[')
            child.forEachIndexed { index, item ->
                if (index > 0) append(',')
                append(item.toJson())
            }
            append(']')
        }
        append('}')
    }
}
