// Generated from docs/kotlin-typealias.schema.json; run scripts/generate-ir-model.py.
package kotc.bir

import org.msgpack.core.MessagePack
import org.msgpack.core.MessagePacker

internal object SourceMetadataMessagePack {
    fun encode(value: SourceTypeAlias): ByteArray = MessagePack.newDefaultBufferPacker().use { packer ->
        writeSourceTypeAlias(value, packer)
        packer.toByteArray()
    }

    private fun writeSourceTypeAlias(value: SourceTypeAlias, packer: MessagePacker) {
        packer.packMapHeader(8)
        packer.packString("packageName")
        MessagePackText.write(packer, value.packageName)
        packer.packString("ownerPath")
        packer.packArrayHeader(value.ownerPath.size)
        value.ownerPath.forEach { item ->
            MessagePackText.write(packer, item)
        }
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("visibility")
        MessagePackText.write(packer, value.visibility)
        packer.packString("typeParameters")
        packer.packArrayHeader(value.typeParameters.size)
        value.typeParameters.forEach { item ->
            writeSourceTypeParameter(item, packer)
        }
        packer.packString("underlyingType")
        writeSourceType(value.underlyingType, packer)
        packer.packString("expandedType")
        writeSourceType(value.expandedType, packer)
        packer.packString("annotations")
        packer.packArrayHeader(value.annotations.size)
        value.annotations.forEach { item ->
            writeSourceAnnotation(item, packer)
        }
    }

    private fun writeSourceClassifier(value: SourceClassifier, packer: MessagePacker) {
        packer.packMapHeader(3)
        packer.packString("packageName")
        MessagePackText.write(packer, value.packageName)
        packer.packString("ownerPath")
        packer.packArrayHeader(value.ownerPath.size)
        value.ownerPath.forEach { item ->
            MessagePackText.write(packer, item)
        }
        packer.packString("name")
        MessagePackText.write(packer, value.name)
    }

    private fun writeSourceTypeParameter(value: SourceTypeParameter, packer: MessagePacker) {
        packer.packMapHeader(5)
        packer.packString("id")
        packer.packInt(value.id)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("variance")
        MessagePackText.write(packer, value.variance)
        packer.packString("bounds")
        packer.packArrayHeader(value.bounds.size)
        value.bounds.forEach { item ->
            writeSourceType(item, packer)
        }
        packer.packString("annotations")
        packer.packArrayHeader(value.annotations.size)
        value.annotations.forEach { item ->
            writeSourceAnnotation(item, packer)
        }
    }

    private fun writeSourceType(value: SourceType, packer: MessagePacker) {
        when (value) {
            is SourceType.ClassType -> writeSourceTypeClassType(value, packer)
            is SourceType.AliasType -> writeSourceTypeAliasType(value, packer)
            is SourceType.ParameterType -> writeSourceTypeParameterType(value, packer)
        }
    }

    private fun writeSourceTypeClassType(value: SourceType.ClassType, packer: MessagePacker) {
        packer.packMapHeader(7 + (if (value.outerType == null) 0 else 1) + (if (value.abbreviatedType == null) 0 else 1) + (if (value.flexibleUpperBound == null) 0 else 1) + (if (value.flexibleCapabilities == null) 0 else 1))
        packer.packString("kind")
        packer.packString("class");
        packer.packString("classifier")
        writeSourceClassifier(value.classifier, packer)
        packer.packString("nullable")
        packer.packBoolean(value.nullable)
        packer.packString("suspend")
        packer.packBoolean(value.suspend)
        packer.packString("definitelyNotNull")
        packer.packBoolean(value.definitelyNotNull)
        packer.packString("arguments")
        packer.packArrayHeader(value.arguments.size)
        value.arguments.forEach { item ->
            writeSourceTypeArgument(item, packer)
        }
        packer.packString("annotations")
        packer.packArrayHeader(value.annotations.size)
        value.annotations.forEach { item ->
            writeSourceAnnotation(item, packer)
        }
        value.outerType?.let { child ->
            packer.packString("outerType")
            writeSourceType(child, packer)
        }
        value.abbreviatedType?.let { child ->
            packer.packString("abbreviatedType")
            writeSourceType(child, packer)
        }
        value.flexibleUpperBound?.let { child ->
            packer.packString("flexibleUpperBound")
            writeSourceType(child, packer)
        }
        value.flexibleCapabilities?.let { child ->
            packer.packString("flexibleCapabilities")
            MessagePackText.write(packer, child)
        }
    }

    private fun writeSourceTypeAliasType(value: SourceType.AliasType, packer: MessagePacker) {
        packer.packMapHeader(7 + (if (value.outerType == null) 0 else 1) + (if (value.abbreviatedType == null) 0 else 1) + (if (value.flexibleUpperBound == null) 0 else 1) + (if (value.flexibleCapabilities == null) 0 else 1))
        packer.packString("kind")
        packer.packString("alias");
        packer.packString("classifier")
        writeSourceClassifier(value.classifier, packer)
        packer.packString("nullable")
        packer.packBoolean(value.nullable)
        packer.packString("suspend")
        packer.packBoolean(value.suspend)
        packer.packString("definitelyNotNull")
        packer.packBoolean(value.definitelyNotNull)
        packer.packString("arguments")
        packer.packArrayHeader(value.arguments.size)
        value.arguments.forEach { item ->
            writeSourceTypeArgument(item, packer)
        }
        packer.packString("annotations")
        packer.packArrayHeader(value.annotations.size)
        value.annotations.forEach { item ->
            writeSourceAnnotation(item, packer)
        }
        value.outerType?.let { child ->
            packer.packString("outerType")
            writeSourceType(child, packer)
        }
        value.abbreviatedType?.let { child ->
            packer.packString("abbreviatedType")
            writeSourceType(child, packer)
        }
        value.flexibleUpperBound?.let { child ->
            packer.packString("flexibleUpperBound")
            writeSourceType(child, packer)
        }
        value.flexibleCapabilities?.let { child ->
            packer.packString("flexibleCapabilities")
            MessagePackText.write(packer, child)
        }
    }

    private fun writeSourceTypeParameterType(value: SourceType.ParameterType, packer: MessagePacker) {
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
        packer.packArrayHeader(value.arguments.size)
        value.arguments.forEach { item ->
            writeSourceTypeArgument(item, packer)
        }
        packer.packString("annotations")
        packer.packArrayHeader(value.annotations.size)
        value.annotations.forEach { item ->
            writeSourceAnnotation(item, packer)
        }
        value.outerType?.let { child ->
            packer.packString("outerType")
            writeSourceType(child, packer)
        }
        value.abbreviatedType?.let { child ->
            packer.packString("abbreviatedType")
            writeSourceType(child, packer)
        }
        value.flexibleUpperBound?.let { child ->
            packer.packString("flexibleUpperBound")
            writeSourceType(child, packer)
        }
        value.flexibleCapabilities?.let { child ->
            packer.packString("flexibleCapabilities")
            MessagePackText.write(packer, child)
        }
    }

    private fun writeSourceTypeArgument(value: SourceTypeArgument, packer: MessagePacker) {
        when (value) {
            is SourceTypeArgument.InvArgument -> writeSourceTypeArgumentInvArgument(value, packer)
            is SourceTypeArgument.InArgument -> writeSourceTypeArgumentInArgument(value, packer)
            is SourceTypeArgument.OutArgument -> writeSourceTypeArgumentOutArgument(value, packer)
            is SourceTypeArgument.StarArgument -> writeSourceTypeArgumentStarArgument(value, packer)
        }
    }

    private fun writeSourceTypeArgumentInvArgument(value: SourceTypeArgument.InvArgument, packer: MessagePacker) {
        packer.packMapHeader(2)
        packer.packString("variance")
        packer.packString("inv");
        packer.packString("type")
        writeSourceType(value.type, packer)
    }

    private fun writeSourceTypeArgumentInArgument(value: SourceTypeArgument.InArgument, packer: MessagePacker) {
        packer.packMapHeader(2)
        packer.packString("variance")
        packer.packString("in");
        packer.packString("type")
        writeSourceType(value.type, packer)
    }

    private fun writeSourceTypeArgumentOutArgument(value: SourceTypeArgument.OutArgument, packer: MessagePacker) {
        packer.packMapHeader(2)
        packer.packString("variance")
        packer.packString("out");
        packer.packString("type")
        writeSourceType(value.type, packer)
    }

    private fun writeSourceTypeArgumentStarArgument(value: SourceTypeArgument.StarArgument, packer: MessagePacker) {
        packer.packMapHeader(1)
        packer.packString("variance")
        packer.packString("star");
    }

    private fun writeSourceAnnotation(value: SourceAnnotation, packer: MessagePacker) {
        packer.packMapHeader(2)
        packer.packString("classifier")
        writeSourceClassifier(value.classifier, packer)
        packer.packString("arguments")
        packer.packArrayHeader(value.arguments.size)
        value.arguments.forEach { item ->
            writeSourceAnnotationArgument(item, packer)
        }
    }

    private fun writeSourceAnnotationArgument(value: SourceAnnotationArgument, packer: MessagePacker) {
        packer.packMapHeader(2)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("value")
        writeSourceAnnotationValue(value.value, packer)
    }

    private fun writeSourceAnnotationValue(value: SourceAnnotationValue, packer: MessagePacker) {
        when (value) {
            is SourceAnnotationValue.ByteValue -> writeSourceAnnotationValueByteValue(value, packer)
            is SourceAnnotationValue.CharValue -> writeSourceAnnotationValueCharValue(value, packer)
            is SourceAnnotationValue.ShortValue -> writeSourceAnnotationValueShortValue(value, packer)
            is SourceAnnotationValue.IntValue -> writeSourceAnnotationValueIntValue(value, packer)
            is SourceAnnotationValue.LongValue -> writeSourceAnnotationValueLongValue(value, packer)
            is SourceAnnotationValue.BooleanValue -> writeSourceAnnotationValueBooleanValue(value, packer)
            is SourceAnnotationValue.StringValue -> writeSourceAnnotationValueStringValue(value, packer)
            is SourceAnnotationValue.FloatValue -> writeSourceAnnotationValueFloatValue(value, packer)
            is SourceAnnotationValue.DoubleValue -> writeSourceAnnotationValueDoubleValue(value, packer)
            is SourceAnnotationValue.ClassValue -> writeSourceAnnotationValueClassValue(value, packer)
            is SourceAnnotationValue.EnumValue -> writeSourceAnnotationValueEnumValue(value, packer)
            is SourceAnnotationValue.AnnotationValue -> writeSourceAnnotationValueAnnotationValue(value, packer)
            is SourceAnnotationValue.ArrayValue -> writeSourceAnnotationValueArrayValue(value, packer)
        }
    }

    private fun writeSourceAnnotationValueByteValue(value: SourceAnnotationValue.ByteValue, packer: MessagePacker) {
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("byte");
        packer.packString("value")
        packer.packLong(value.value)
    }

    private fun writeSourceAnnotationValueCharValue(value: SourceAnnotationValue.CharValue, packer: MessagePacker) {
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("char");
        packer.packString("value")
        packer.packLong(value.value)
    }

    private fun writeSourceAnnotationValueShortValue(value: SourceAnnotationValue.ShortValue, packer: MessagePacker) {
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("short");
        packer.packString("value")
        packer.packLong(value.value)
    }

    private fun writeSourceAnnotationValueIntValue(value: SourceAnnotationValue.IntValue, packer: MessagePacker) {
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("int");
        packer.packString("value")
        packer.packLong(value.value)
    }

    private fun writeSourceAnnotationValueLongValue(value: SourceAnnotationValue.LongValue, packer: MessagePacker) {
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("long");
        packer.packString("value")
        packer.packLong(value.value)
    }

    private fun writeSourceAnnotationValueBooleanValue(value: SourceAnnotationValue.BooleanValue, packer: MessagePacker) {
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("boolean");
        packer.packString("value")
        packer.packLong(value.value)
    }

    private fun writeSourceAnnotationValueStringValue(value: SourceAnnotationValue.StringValue, packer: MessagePacker) {
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("string");
        packer.packString("value")
        MessagePackText.write(packer, value.value)
    }

    private fun writeSourceAnnotationValueFloatValue(value: SourceAnnotationValue.FloatValue, packer: MessagePacker) {
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("float");
        packer.packString("bits")
        packer.packLong(value.bits)
    }

    private fun writeSourceAnnotationValueDoubleValue(value: SourceAnnotationValue.DoubleValue, packer: MessagePacker) {
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("double");
        packer.packString("bits")
        packer.packLong(value.bits)
    }

    private fun writeSourceAnnotationValueClassValue(value: SourceAnnotationValue.ClassValue, packer: MessagePacker) {
        packer.packMapHeader(4)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("class");
        packer.packString("classifier")
        writeSourceClassifier(value.classifier, packer)
        packer.packString("arrayDimensions")
        packer.packInt(value.arrayDimensions)
    }

    private fun writeSourceAnnotationValueEnumValue(value: SourceAnnotationValue.EnumValue, packer: MessagePacker) {
        packer.packMapHeader(4)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("enum");
        packer.packString("classifier")
        writeSourceClassifier(value.classifier, packer)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
    }

    private fun writeSourceAnnotationValueAnnotationValue(value: SourceAnnotationValue.AnnotationValue, packer: MessagePacker) {
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("annotation");
        packer.packString("value")
        writeSourceAnnotation(value.value, packer)
    }

    private fun writeSourceAnnotationValueArrayValue(value: SourceAnnotationValue.ArrayValue, packer: MessagePacker) {
        packer.packMapHeader(3)
        packer.packString("unsigned")
        packer.packBoolean(value.unsigned)
        packer.packString("kind")
        packer.packString("array");
        packer.packString("value")
        packer.packArrayHeader(value.value.size)
        value.value.forEach { item ->
            writeSourceAnnotationValue(item, packer)
        }
    }

}
