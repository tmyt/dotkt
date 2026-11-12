// Generated from docs/kotlin-typealias.schema.json; run scripts/generate-ir-model.py.
package kotc.bir

import org.msgpack.core.MessagePack
import org.msgpack.core.MessagePacker

internal object SourceMetadataMessagePack {
    fun encode(value: SourceTypeAlias): ByteArray = MessagePack.newDefaultBufferPacker().use { packer ->
        write(value, packer)
        packer.toByteArray()
    }
    fun write(value: SourceTypeAlias, packer: MessagePacker, depth: Int = 0) = writeSourceTypeAlias(value, packer, depth)
    private fun checkDepth(depth: Int) { require(depth < 1024) { "BIR nesting exceeds 1024" } }

    private fun writeSourceTypeAlias(value: SourceTypeAlias, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(8)
        packer.packString("packageName")
        MessagePackText.write(packer, value.packageName)
        packer.packString("ownerPath")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.ownerPath.size)
        value.ownerPath.forEach { item ->
            MessagePackText.write(packer, item)
        }
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("visibility")
        MessagePackText.write(packer, value.visibility)
        packer.packString("typeParameters")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.typeParameters.size)
        value.typeParameters.forEach { item ->
            writeSourceTypeParameter(item, packer, depth + 2)
        }
        packer.packString("underlyingType")
        writeSourceType(value.underlyingType, packer, depth + 1)
        packer.packString("expandedType")
        writeSourceType(value.expandedType, packer, depth + 1)
        packer.packString("annotations")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.annotations.size)
        value.annotations.forEach { item ->
            writeSourceAnnotation(item, packer, depth + 2)
        }
    }

    private fun writeSourceClassifier(value: SourceClassifier, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("packageName")
        MessagePackText.write(packer, value.packageName)
        packer.packString("ownerPath")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.ownerPath.size)
        value.ownerPath.forEach { item ->
            MessagePackText.write(packer, item)
        }
        packer.packString("name")
        MessagePackText.write(packer, value.name)
    }

    private fun writeSourceTypeParameter(value: SourceTypeParameter, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(5)
        packer.packString("id")
        packer.packInt(value.id)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("variance")
        MessagePackText.write(packer, value.variance)
        packer.packString("bounds")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.bounds.size)
        value.bounds.forEach { item ->
            writeSourceType(item, packer, depth + 2)
        }
        packer.packString("annotations")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.annotations.size)
        value.annotations.forEach { item ->
            writeSourceAnnotation(item, packer, depth + 2)
        }
    }

    private fun writeSourceType(value: SourceType, packer: MessagePacker, depth: Int) {
        when (value) {
            is SourceType.ClassType -> writeSourceTypeClassType(value, packer, depth)
            is SourceType.AliasType -> writeSourceTypeAliasType(value, packer, depth)
            is SourceType.ParameterType -> writeSourceTypeParameterType(value, packer, depth)
        }
    }

    private fun writeSourceTypeClassType(value: SourceType.ClassType, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(7 + (if (value.outerType == null) 0 else 1) + (if (value.abbreviatedType == null) 0 else 1) + (if (value.flexibleUpperBound == null) 0 else 1) + (if (value.flexibleCapabilities == null) 0 else 1))
        packer.packString("kind")
        packer.packString("class");
        packer.packString("classifier")
        writeSourceClassifier(value.classifier, packer, depth + 1)
        packer.packString("nullable")
        packer.packBoolean(value.nullable)
        packer.packString("suspend")
        packer.packBoolean(value.suspend)
        packer.packString("definitelyNotNull")
        packer.packBoolean(value.definitelyNotNull)
        packer.packString("arguments")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.arguments.size)
        value.arguments.forEach { item ->
            writeSourceTypeArgument(item, packer, depth + 2)
        }
        packer.packString("annotations")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.annotations.size)
        value.annotations.forEach { item ->
            writeSourceAnnotation(item, packer, depth + 2)
        }
        value.outerType?.let { child ->
            packer.packString("outerType")
            writeSourceType(child, packer, depth + 1)
        }
        value.abbreviatedType?.let { child ->
            packer.packString("abbreviatedType")
            writeSourceType(child, packer, depth + 1)
        }
        value.flexibleUpperBound?.let { child ->
            packer.packString("flexibleUpperBound")
            writeSourceType(child, packer, depth + 1)
        }
        value.flexibleCapabilities?.let { child ->
            packer.packString("flexibleCapabilities")
            MessagePackText.write(packer, child)
        }
    }

    private fun writeSourceTypeAliasType(value: SourceType.AliasType, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(7 + (if (value.outerType == null) 0 else 1) + (if (value.abbreviatedType == null) 0 else 1) + (if (value.flexibleUpperBound == null) 0 else 1) + (if (value.flexibleCapabilities == null) 0 else 1))
        packer.packString("kind")
        packer.packString("alias");
        packer.packString("classifier")
        writeSourceClassifier(value.classifier, packer, depth + 1)
        packer.packString("nullable")
        packer.packBoolean(value.nullable)
        packer.packString("suspend")
        packer.packBoolean(value.suspend)
        packer.packString("definitelyNotNull")
        packer.packBoolean(value.definitelyNotNull)
        packer.packString("arguments")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.arguments.size)
        value.arguments.forEach { item ->
            writeSourceTypeArgument(item, packer, depth + 2)
        }
        packer.packString("annotations")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.annotations.size)
        value.annotations.forEach { item ->
            writeSourceAnnotation(item, packer, depth + 2)
        }
        value.outerType?.let { child ->
            packer.packString("outerType")
            writeSourceType(child, packer, depth + 1)
        }
        value.abbreviatedType?.let { child ->
            packer.packString("abbreviatedType")
            writeSourceType(child, packer, depth + 1)
        }
        value.flexibleUpperBound?.let { child ->
            packer.packString("flexibleUpperBound")
            writeSourceType(child, packer, depth + 1)
        }
        value.flexibleCapabilities?.let { child ->
            packer.packString("flexibleCapabilities")
            MessagePackText.write(packer, child)
        }
    }

    private fun writeSourceTypeParameterType(value: SourceType.ParameterType, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(7 + (if (value.outerType == null) 0 else 1) + (if (value.abbreviatedType == null) 0 else 1) + (if (value.flexibleUpperBound == null) 0 else 1) + (if (value.flexibleCapabilities == null) 0 else 1))
        packer.packString("kind")
        packer.packString("parameter");
        packer.packString("id")
        packer.packInt(value.id)
        packer.packString("nullable")
        packer.packBoolean(value.nullable)
        packer.packString("suspend")
        packer.packBoolean(value.suspend)
        packer.packString("definitelyNotNull")
        packer.packBoolean(value.definitelyNotNull)
        packer.packString("arguments")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.arguments.size)
        value.arguments.forEach { item ->
            writeSourceTypeArgument(item, packer, depth + 2)
        }
        packer.packString("annotations")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.annotations.size)
        value.annotations.forEach { item ->
            writeSourceAnnotation(item, packer, depth + 2)
        }
        value.outerType?.let { child ->
            packer.packString("outerType")
            writeSourceType(child, packer, depth + 1)
        }
        value.abbreviatedType?.let { child ->
            packer.packString("abbreviatedType")
            writeSourceType(child, packer, depth + 1)
        }
        value.flexibleUpperBound?.let { child ->
            packer.packString("flexibleUpperBound")
            writeSourceType(child, packer, depth + 1)
        }
        value.flexibleCapabilities?.let { child ->
            packer.packString("flexibleCapabilities")
            MessagePackText.write(packer, child)
        }
    }

    private fun writeSourceTypeArgument(value: SourceTypeArgument, packer: MessagePacker, depth: Int) {
        when (value) {
            is SourceTypeArgument.InvArgument -> writeSourceTypeArgumentInvArgument(value, packer, depth)
            is SourceTypeArgument.InArgument -> writeSourceTypeArgumentInArgument(value, packer, depth)
            is SourceTypeArgument.OutArgument -> writeSourceTypeArgumentOutArgument(value, packer, depth)
            is SourceTypeArgument.StarArgument -> writeSourceTypeArgumentStarArgument(value, packer, depth)
        }
    }

    private fun writeSourceTypeArgumentInvArgument(value: SourceTypeArgument.InvArgument, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("variance")
        packer.packString("inv");
        packer.packString("type")
        writeSourceType(value.type, packer, depth + 1)
    }

    private fun writeSourceTypeArgumentInArgument(value: SourceTypeArgument.InArgument, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("variance")
        packer.packString("in");
        packer.packString("type")
        writeSourceType(value.type, packer, depth + 1)
    }

    private fun writeSourceTypeArgumentOutArgument(value: SourceTypeArgument.OutArgument, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("variance")
        packer.packString("out");
        packer.packString("type")
        writeSourceType(value.type, packer, depth + 1)
    }

    private fun writeSourceTypeArgumentStarArgument(value: SourceTypeArgument.StarArgument, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(1)
        packer.packString("variance")
        packer.packString("star");
    }

    private fun writeSourceAnnotation(value: SourceAnnotation, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("classifier")
        writeSourceClassifier(value.classifier, packer, depth + 1)
        packer.packString("arguments")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.arguments.size)
        value.arguments.forEach { item ->
            writeSourceAnnotationArgument(item, packer, depth + 2)
        }
    }

    private fun writeSourceAnnotationArgument(value: SourceAnnotationArgument, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("value")
        writeSourceAnnotationValue(value.value, packer, depth + 1)
    }

    private fun writeSourceAnnotationValue(value: SourceAnnotationValue, packer: MessagePacker, depth: Int) {
        when (value) {
            is SourceAnnotationValue.ByteValue -> writeSourceAnnotationValueByteValue(value, packer, depth)
            is SourceAnnotationValue.CharValue -> writeSourceAnnotationValueCharValue(value, packer, depth)
            is SourceAnnotationValue.ShortValue -> writeSourceAnnotationValueShortValue(value, packer, depth)
            is SourceAnnotationValue.IntValue -> writeSourceAnnotationValueIntValue(value, packer, depth)
            is SourceAnnotationValue.LongValue -> writeSourceAnnotationValueLongValue(value, packer, depth)
            is SourceAnnotationValue.BooleanValue -> writeSourceAnnotationValueBooleanValue(value, packer, depth)
            is SourceAnnotationValue.StringValue -> writeSourceAnnotationValueStringValue(value, packer, depth)
            is SourceAnnotationValue.FloatValue -> writeSourceAnnotationValueFloatValue(value, packer, depth)
            is SourceAnnotationValue.DoubleValue -> writeSourceAnnotationValueDoubleValue(value, packer, depth)
            is SourceAnnotationValue.ClassValue -> writeSourceAnnotationValueClassValue(value, packer, depth)
            is SourceAnnotationValue.EnumValue -> writeSourceAnnotationValueEnumValue(value, packer, depth)
            is SourceAnnotationValue.AnnotationValue -> writeSourceAnnotationValueAnnotationValue(value, packer, depth)
            is SourceAnnotationValue.ArrayValue -> writeSourceAnnotationValueArrayValue(value, packer, depth)
        }
    }

    private fun writeSourceAnnotationValueByteValue(value: SourceAnnotationValue.ByteValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("byte");
        packer.packString("value")
        packer.packLong(value.value)
    }

    private fun writeSourceAnnotationValueCharValue(value: SourceAnnotationValue.CharValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("char");
        packer.packString("value")
        packer.packLong(value.value)
    }

    private fun writeSourceAnnotationValueShortValue(value: SourceAnnotationValue.ShortValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("short");
        packer.packString("value")
        packer.packLong(value.value)
    }

    private fun writeSourceAnnotationValueIntValue(value: SourceAnnotationValue.IntValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("int");
        packer.packString("value")
        packer.packLong(value.value)
    }

    private fun writeSourceAnnotationValueLongValue(value: SourceAnnotationValue.LongValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("long");
        packer.packString("value")
        packer.packLong(value.value)
    }

    private fun writeSourceAnnotationValueBooleanValue(value: SourceAnnotationValue.BooleanValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("boolean");
        packer.packString("value")
        packer.packLong(value.value)
    }

    private fun writeSourceAnnotationValueStringValue(value: SourceAnnotationValue.StringValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("string");
        packer.packString("value")
        MessagePackText.write(packer, value.value)
    }

    private fun writeSourceAnnotationValueFloatValue(value: SourceAnnotationValue.FloatValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("float");
        packer.packString("bits")
        packer.packLong(value.bits)
    }

    private fun writeSourceAnnotationValueDoubleValue(value: SourceAnnotationValue.DoubleValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("double");
        packer.packString("bits")
        packer.packLong(value.bits)
    }

    private fun writeSourceAnnotationValueClassValue(value: SourceAnnotationValue.ClassValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("class");
        packer.packString("classifier")
        writeSourceClassifier(value.classifier, packer, depth + 1)
        packer.packString("arrayDimensions")
        packer.packInt(value.arrayDimensions)
    }

    private fun writeSourceAnnotationValueEnumValue(value: SourceAnnotationValue.EnumValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("enum");
        packer.packString("classifier")
        writeSourceClassifier(value.classifier, packer, depth + 1)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
    }

    private fun writeSourceAnnotationValueAnnotationValue(value: SourceAnnotationValue.AnnotationValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("annotation");
        packer.packString("value")
        writeSourceAnnotation(value.value, packer, depth + 1)
    }

    private fun writeSourceAnnotationValueArrayValue(value: SourceAnnotationValue.ArrayValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("array");
        packer.packString("value")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.value.size)
        value.value.forEach { item ->
            writeSourceAnnotationValue(item, packer, depth + 2)
        }
    }

}
