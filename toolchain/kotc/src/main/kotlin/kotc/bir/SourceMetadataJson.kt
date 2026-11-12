// Generated from docs/kotlin-typealias.schema.json; run scripts/generate-ir-model.py.
package kotc.bir

internal object SourceMetadataJson {
    fun encode(value: SourceTypeAlias): String = buildString { write(value, this) }
    fun write(value: SourceTypeAlias, output: StringBuilder) = writeSourceTypeAlias(value, output)

    private fun writeSourceTypeAlias(value: SourceTypeAlias, sb: StringBuilder) {
        sb.append('{')
        sb.append("\"packageName\":")
        TypeNodeJson.writeString(value.packageName, sb)
        sb.append(",\"ownerPath\":")
        sb.append('[')
        value.ownerPath.forEachIndexed { index, item ->
            if (index > 0) sb.append(',')
            TypeNodeJson.writeString(item, sb)
        }
        sb.append(']')
        sb.append(",\"name\":")
        TypeNodeJson.writeString(value.name, sb)
        sb.append(",\"visibility\":")
        TypeNodeJson.writeString(value.visibility, sb)
        sb.append(",\"typeParameters\":")
        sb.append('[')
        value.typeParameters.forEachIndexed { index, item ->
            if (index > 0) sb.append(',')
            writeSourceTypeParameter(item, sb)
        }
        sb.append(']')
        sb.append(",\"underlyingType\":")
        writeSourceType(value.underlyingType, sb)
        sb.append(",\"expandedType\":")
        writeSourceType(value.expandedType, sb)
        sb.append(",\"annotations\":")
        sb.append('[')
        value.annotations.forEachIndexed { index, item ->
            if (index > 0) sb.append(',')
            writeSourceAnnotation(item, sb)
        }
        sb.append(']')
        sb.append('}')
    }

    private fun writeSourceClassifier(value: SourceClassifier, sb: StringBuilder) {
        sb.append('{')
        sb.append("\"packageName\":")
        TypeNodeJson.writeString(value.packageName, sb)
        sb.append(",\"ownerPath\":")
        sb.append('[')
        value.ownerPath.forEachIndexed { index, item ->
            if (index > 0) sb.append(',')
            TypeNodeJson.writeString(item, sb)
        }
        sb.append(']')
        sb.append(",\"name\":")
        TypeNodeJson.writeString(value.name, sb)
        sb.append('}')
    }

    private fun writeSourceTypeParameter(value: SourceTypeParameter, sb: StringBuilder) {
        sb.append('{')
        sb.append("\"id\":")
        sb.append(value.id)
        sb.append(",\"name\":")
        TypeNodeJson.writeString(value.name, sb)
        sb.append(",\"variance\":")
        TypeNodeJson.writeString(value.variance, sb)
        sb.append(",\"bounds\":")
        sb.append('[')
        value.bounds.forEachIndexed { index, item ->
            if (index > 0) sb.append(',')
            writeSourceType(item, sb)
        }
        sb.append(']')
        sb.append(",\"annotations\":")
        sb.append('[')
        value.annotations.forEachIndexed { index, item ->
            if (index > 0) sb.append(',')
            writeSourceAnnotation(item, sb)
        }
        sb.append(']')
        sb.append('}')
    }

    private fun writeSourceType(value: SourceType, sb: StringBuilder) {
        when (value) {
            is SourceType.ClassType -> {
                sb.append('{')
                sb.append("\"kind\":")
                sb.append("\"class\"")
                sb.append(",\"classifier\":")
                writeSourceClassifier(value.classifier, sb)
                sb.append(",\"nullable\":")
                sb.append(value.nullable)
                sb.append(",\"suspend\":")
                sb.append(value.suspend)
                sb.append(",\"definitelyNotNull\":")
                sb.append(value.definitelyNotNull)
                sb.append(",\"arguments\":")
                sb.append('[')
                value.arguments.forEachIndexed { index, item ->
                    if (index > 0) sb.append(',')
                    writeSourceTypeArgument(item, sb)
                }
                sb.append(']')
                sb.append(",\"annotations\":")
                sb.append('[')
                value.annotations.forEachIndexed { index, item ->
                    if (index > 0) sb.append(',')
                    writeSourceAnnotation(item, sb)
                }
                sb.append(']')
                value.outerType?.let { child ->
                    sb.append(",\"outerType\":")
                    writeSourceType(child, sb)
                }
                value.abbreviatedType?.let { child ->
                    sb.append(",\"abbreviatedType\":")
                    writeSourceType(child, sb)
                }
                value.flexibleUpperBound?.let { child ->
                    sb.append(",\"flexibleUpperBound\":")
                    writeSourceType(child, sb)
                }
                value.flexibleCapabilities?.let { child ->
                    sb.append(",\"flexibleCapabilities\":")
                    TypeNodeJson.writeString(child, sb)
                }
                sb.append('}')
            }
            is SourceType.AliasType -> {
                sb.append('{')
                sb.append("\"kind\":")
                sb.append("\"alias\"")
                sb.append(",\"classifier\":")
                writeSourceClassifier(value.classifier, sb)
                sb.append(",\"nullable\":")
                sb.append(value.nullable)
                sb.append(",\"suspend\":")
                sb.append(value.suspend)
                sb.append(",\"definitelyNotNull\":")
                sb.append(value.definitelyNotNull)
                sb.append(",\"arguments\":")
                sb.append('[')
                value.arguments.forEachIndexed { index, item ->
                    if (index > 0) sb.append(',')
                    writeSourceTypeArgument(item, sb)
                }
                sb.append(']')
                sb.append(",\"annotations\":")
                sb.append('[')
                value.annotations.forEachIndexed { index, item ->
                    if (index > 0) sb.append(',')
                    writeSourceAnnotation(item, sb)
                }
                sb.append(']')
                value.outerType?.let { child ->
                    sb.append(",\"outerType\":")
                    writeSourceType(child, sb)
                }
                value.abbreviatedType?.let { child ->
                    sb.append(",\"abbreviatedType\":")
                    writeSourceType(child, sb)
                }
                value.flexibleUpperBound?.let { child ->
                    sb.append(",\"flexibleUpperBound\":")
                    writeSourceType(child, sb)
                }
                value.flexibleCapabilities?.let { child ->
                    sb.append(",\"flexibleCapabilities\":")
                    TypeNodeJson.writeString(child, sb)
                }
                sb.append('}')
            }
            is SourceType.ParameterType -> {
                sb.append('{')
                sb.append("\"kind\":")
                sb.append("\"parameter\"")
                sb.append(",\"id\":")
                sb.append(value.id)
                sb.append(",\"nullable\":")
                sb.append(value.nullable)
                sb.append(",\"suspend\":")
                sb.append(value.suspend)
                sb.append(",\"definitelyNotNull\":")
                sb.append(value.definitelyNotNull)
                sb.append(",\"arguments\":")
                sb.append('[')
                value.arguments.forEachIndexed { index, item ->
                    if (index > 0) sb.append(',')
                    writeSourceTypeArgument(item, sb)
                }
                sb.append(']')
                sb.append(",\"annotations\":")
                sb.append('[')
                value.annotations.forEachIndexed { index, item ->
                    if (index > 0) sb.append(',')
                    writeSourceAnnotation(item, sb)
                }
                sb.append(']')
                value.outerType?.let { child ->
                    sb.append(",\"outerType\":")
                    writeSourceType(child, sb)
                }
                value.abbreviatedType?.let { child ->
                    sb.append(",\"abbreviatedType\":")
                    writeSourceType(child, sb)
                }
                value.flexibleUpperBound?.let { child ->
                    sb.append(",\"flexibleUpperBound\":")
                    writeSourceType(child, sb)
                }
                value.flexibleCapabilities?.let { child ->
                    sb.append(",\"flexibleCapabilities\":")
                    TypeNodeJson.writeString(child, sb)
                }
                sb.append('}')
            }
        }
    }

    private fun writeSourceTypeArgument(value: SourceTypeArgument, sb: StringBuilder) {
        when (value) {
            is SourceTypeArgument.InvArgument -> {
                sb.append('{')
                sb.append("\"variance\":")
                sb.append("\"inv\"")
                sb.append(",\"type\":")
                writeSourceType(value.type, sb)
                sb.append('}')
            }
            is SourceTypeArgument.InArgument -> {
                sb.append('{')
                sb.append("\"variance\":")
                sb.append("\"in\"")
                sb.append(",\"type\":")
                writeSourceType(value.type, sb)
                sb.append('}')
            }
            is SourceTypeArgument.OutArgument -> {
                sb.append('{')
                sb.append("\"variance\":")
                sb.append("\"out\"")
                sb.append(",\"type\":")
                writeSourceType(value.type, sb)
                sb.append('}')
            }
            SourceTypeArgument.StarArgument -> {
                sb.append('{')
                sb.append("\"variance\":")
                sb.append("\"star\"")
                sb.append('}')
            }
        }
    }

    private fun writeSourceAnnotation(value: SourceAnnotation, sb: StringBuilder) {
        sb.append('{')
        sb.append("\"classifier\":")
        writeSourceClassifier(value.classifier, sb)
        sb.append(",\"arguments\":")
        sb.append('[')
        value.arguments.forEachIndexed { index, item ->
            if (index > 0) sb.append(',')
            writeSourceAnnotationArgument(item, sb)
        }
        sb.append(']')
        sb.append('}')
    }

    private fun writeSourceAnnotationArgument(value: SourceAnnotationArgument, sb: StringBuilder) {
        sb.append('{')
        sb.append("\"name\":")
        TypeNodeJson.writeString(value.name, sb)
        sb.append(",\"value\":")
        writeSourceAnnotationValue(value.value, sb)
        sb.append('}')
    }

    private fun writeSourceAnnotationValue(value: SourceAnnotationValue, sb: StringBuilder) {
        when (value) {
            is SourceAnnotationValue.ByteValue -> {
                sb.append('{')
                sb.append("\"unsigned\":")
                sb.append(value.unsigned)
                sb.append(",\"kind\":")
                sb.append("\"byte\"")
                sb.append(",\"value\":")
                sb.append(value.value)
                sb.append('}')
            }
            is SourceAnnotationValue.CharValue -> {
                sb.append('{')
                sb.append("\"unsigned\":")
                sb.append(value.unsigned)
                sb.append(",\"kind\":")
                sb.append("\"char\"")
                sb.append(",\"value\":")
                sb.append(value.value)
                sb.append('}')
            }
            is SourceAnnotationValue.ShortValue -> {
                sb.append('{')
                sb.append("\"unsigned\":")
                sb.append(value.unsigned)
                sb.append(",\"kind\":")
                sb.append("\"short\"")
                sb.append(",\"value\":")
                sb.append(value.value)
                sb.append('}')
            }
            is SourceAnnotationValue.IntValue -> {
                sb.append('{')
                sb.append("\"unsigned\":")
                sb.append(value.unsigned)
                sb.append(",\"kind\":")
                sb.append("\"int\"")
                sb.append(",\"value\":")
                sb.append(value.value)
                sb.append('}')
            }
            is SourceAnnotationValue.LongValue -> {
                sb.append('{')
                sb.append("\"unsigned\":")
                sb.append(value.unsigned)
                sb.append(",\"kind\":")
                sb.append("\"long\"")
                sb.append(",\"value\":")
                sb.append(value.value)
                sb.append('}')
            }
            is SourceAnnotationValue.BooleanValue -> {
                sb.append('{')
                sb.append("\"unsigned\":")
                sb.append(value.unsigned)
                sb.append(",\"kind\":")
                sb.append("\"boolean\"")
                sb.append(",\"value\":")
                sb.append(value.value)
                sb.append('}')
            }
            is SourceAnnotationValue.StringValue -> {
                sb.append('{')
                sb.append("\"unsigned\":")
                sb.append(value.unsigned)
                sb.append(",\"kind\":")
                sb.append("\"string\"")
                sb.append(",\"value\":")
                LiteralText.writeJson(value.value, sb)
                sb.append('}')
            }
            is SourceAnnotationValue.FloatValue -> {
                sb.append('{')
                sb.append("\"unsigned\":")
                sb.append(value.unsigned)
                sb.append(",\"kind\":")
                sb.append("\"float\"")
                sb.append(",\"bits\":")
                sb.append(value.bits)
                sb.append('}')
            }
            is SourceAnnotationValue.DoubleValue -> {
                sb.append('{')
                sb.append("\"unsigned\":")
                sb.append(value.unsigned)
                sb.append(",\"kind\":")
                sb.append("\"double\"")
                sb.append(",\"bits\":")
                sb.append(value.bits)
                sb.append('}')
            }
            is SourceAnnotationValue.ClassValue -> {
                sb.append('{')
                sb.append("\"unsigned\":")
                sb.append(value.unsigned)
                sb.append(",\"kind\":")
                sb.append("\"class\"")
                sb.append(",\"classifier\":")
                writeSourceClassifier(value.classifier, sb)
                sb.append(",\"arrayDimensions\":")
                sb.append(value.arrayDimensions)
                sb.append('}')
            }
            is SourceAnnotationValue.EnumValue -> {
                sb.append('{')
                sb.append("\"unsigned\":")
                sb.append(value.unsigned)
                sb.append(",\"kind\":")
                sb.append("\"enum\"")
                sb.append(",\"classifier\":")
                writeSourceClassifier(value.classifier, sb)
                sb.append(",\"name\":")
                TypeNodeJson.writeString(value.name, sb)
                sb.append('}')
            }
            is SourceAnnotationValue.AnnotationValue -> {
                sb.append('{')
                sb.append("\"unsigned\":")
                sb.append(value.unsigned)
                sb.append(",\"kind\":")
                sb.append("\"annotation\"")
                sb.append(",\"value\":")
                writeSourceAnnotation(value.value, sb)
                sb.append('}')
            }
            is SourceAnnotationValue.ArrayValue -> {
                sb.append('{')
                sb.append("\"unsigned\":")
                sb.append(value.unsigned)
                sb.append(",\"kind\":")
                sb.append("\"array\"")
                sb.append(",\"value\":")
                sb.append('[')
                value.value.forEachIndexed { index, item ->
                    if (index > 0) sb.append(',')
                    writeSourceAnnotationValue(item, sb)
                }
                sb.append(']')
                sb.append('}')
            }
        }
    }

}
