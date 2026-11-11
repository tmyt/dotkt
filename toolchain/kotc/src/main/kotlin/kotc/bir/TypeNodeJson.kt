package kotc.bir

/** JSON boundary encoding for the schema-generated Kotlin type model. No CLR representation is chosen here. */
internal object TypeNodeJson {
    fun encode(type: TypeNode): String = StringBuilder().also { write(type, it) }.toString()

    private fun write(type: TypeNode, sb: StringBuilder) {
        with(type) {
            when (this) {
                is TypeNode.Fqn -> {
                    sb.append("{\"t\":\"fqn\",\"name\":").append(esc(name))
                    if (args != null) {
                        sb.append(",\"args\":")
                        writeArray(sb, args)
                    }
                    classifier?.let { source ->
                        sb.append(",\"classifier\":{\"packageName\":").append(esc(source.packageName))
                        sb.append(",\"ownerPath\":[")
                        source.ownerPath.forEachIndexed { index, owner ->
                            if (index != 0) sb.append(',')
                            sb.append(esc(owner))
                        }
                        sb.append("],\"name\":").append(esc(source.name)).append('}')
                    }
                    sb.append('}')
                }
                is TypeNode.Tv -> sb.append("{\"t\":\"tv\",\"scope\":").append(esc(scope)).append(",\"i\":").append(i).append('}')
                TypeNode.Star -> sb.append("{\"t\":\"star\"}")
                is TypeNode.Projection -> {
                    sb.append("{\"t\":\"projection\",\"variance\":").append(esc(variance)).append(",\"of\":")
                    write(of, sb)
                    sb.append('}')
                }
                is TypeNode.Fn -> {
                    sb.append("{\"t\":\"fn\",\"suspend\":").append(if (suspend) "true" else "false")
                    sb.append(",\"ret\":"); write(ret, sb)
                    sb.append(",\"params\":"); writeArray(sb, params)
                    if (recv != null) { sb.append(",\"recv\":"); write(recv, sb) }
                    sb.append('}')
                }
                is TypeNode.Nullable -> { sb.append("{\"t\":\"nullable\",\"of\":"); write(of, sb); sb.append('}') }
                is TypeNode.Oblivious -> { sb.append("{\"t\":\"oblivious\",\"of\":"); write(of, sb); sb.append('}') }
                is TypeNode.Array -> { sb.append("{\"t\":\"array\",\"elem\":"); write(elem, sb); sb.append('}') }
                is TypeNode.ByRef -> { sb.append("{\"t\":\"byRef\",\"of\":"); write(of, sb); sb.append('}') }
            }
        }
    }

    private fun writeArray(sb: StringBuilder, types: List<TypeNode>) {
        sb.append('[')
        for ((index, type) in types.withIndex()) {
            if (index > 0) sb.append(',')
            write(type, sb)
        }
        sb.append(']')
    }

    private fun esc(value: String): String {
        val out = StringBuilder(value.length + 2)
        out.append('"')
        for (char in value) {
            when (char) {
                '\\' -> out.append("\\\\")
                '"' -> out.append("\\\"")
                '\n' -> out.append("\\n")
                '\r' -> out.append("\\r")
                '\t' -> out.append("\\t")
                '\b' -> out.append("\\b")
                '\u000C' -> out.append("\\f")
                else -> if (char.code < 0x20) out.append("\\u").append(char.code.toString(16).padStart(4, '0'))
                        else out.append(char)
            }
        }
        out.append('"')
        return out.toString()
    }
}
