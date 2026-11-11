// Generated from docs/bir-cir.schema.json by scripts/generate-ir-model.py.
package kotc.bir

internal object TypeNodeJson {
    fun encode(type: TypeNode): String = buildString { write(type, this) }

    private fun write(value: TypeNode, sb: StringBuilder) {
        when (value) {
            is TypeNode.Fqn -> with(value) {
                sb.append("{\"t\":\"fqn\"")
                sb.append(",\"name\":")
                writeString(name, sb)
                if (args != null) {
                    sb.append(",\"args\":")
                    sb.append('[')
                    args.forEachIndexed { index, item ->
                        if (index > 0) sb.append(',')
                        write(item, sb)
                    }
                    sb.append(']')
                }
                if (classifier != null) {
                    sb.append(",\"classifier\":")
                    writeSourceClassifier(classifier, sb)
                }
                sb.append('}')
            }
            is TypeNode.Tv -> with(value) {
                sb.append("{\"t\":\"tv\"")
                sb.append(",\"scope\":")
                writeString(scope, sb)
                sb.append(",\"i\":")
                sb.append(i)
                sb.append('}')
            }
            TypeNode.Star -> with(value) {
                sb.append("{\"t\":\"star\"")
                sb.append('}')
            }
            is TypeNode.Projection -> with(value) {
                sb.append("{\"t\":\"projection\"")
                sb.append(",\"variance\":")
                writeString(variance, sb)
                sb.append(",\"of\":")
                write(of, sb)
                sb.append('}')
            }
            is TypeNode.Fn -> with(value) {
                sb.append("{\"t\":\"fn\"")
                sb.append(",\"suspend\":")
                sb.append(suspend)
                sb.append(",\"ret\":")
                write(ret, sb)
                sb.append(",\"params\":")
                sb.append('[')
                params.forEachIndexed { index, item ->
                    if (index > 0) sb.append(',')
                    write(item, sb)
                }
                sb.append(']')
                if (recv != null) {
                    sb.append(",\"recv\":")
                    write(recv, sb)
                }
                sb.append('}')
            }
            is TypeNode.Nullable -> with(value) {
                sb.append("{\"t\":\"nullable\"")
                sb.append(",\"of\":")
                write(of, sb)
                sb.append('}')
            }
            is TypeNode.Oblivious -> with(value) {
                sb.append("{\"t\":\"oblivious\"")
                sb.append(",\"of\":")
                write(of, sb)
                sb.append('}')
            }
            is TypeNode.Array -> with(value) {
                sb.append("{\"t\":\"array\"")
                sb.append(",\"elem\":")
                write(elem, sb)
                sb.append('}')
            }
            is TypeNode.ByRef -> with(value) {
                sb.append("{\"t\":\"byRef\"")
                sb.append(",\"of\":")
                write(of, sb)
                sb.append('}')
            }
        }
    }

    private fun writeSourceClassifier(value: TypeNode.SourceClassifier, sb: StringBuilder) = with(value) {
        sb.append("{")
        sb.append("\"packageName\":")
        writeString(packageName, sb)
        sb.append(",\"ownerPath\":")
        sb.append('[')
        ownerPath.forEachIndexed { index, item ->
            if (index > 0) sb.append(',')
            writeString(item, sb)
        }
        sb.append(']')
        sb.append(",\"name\":")
        writeString(name, sb)
        sb.append('}')
    }

    private fun writeString(value: String, sb: StringBuilder) {
        sb.append('"')
        for (char in value) {
            when (char) {
                '\\' -> sb.append("\\\\")
                '"' -> sb.append("\\\"")
                '\n' -> sb.append("\\n")
                '\r' -> sb.append("\\r")
                '\t' -> sb.append("\\t")
                '\b' -> sb.append("\\b")
                '\u000C' -> sb.append("\\f")
                else -> if (char.code < 0x20) sb.append("\\u").append(char.code.toString(16).padStart(4, '0'))
                        else sb.append(char)
            }
        }
        sb.append('"')
    }
}
