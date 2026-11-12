// Generated from docs/kotlin-typealias.schema.json; run scripts/generate-ir-model.py.
package kotc.bir

data class SourceTypeAlias(
    val packageName: String,
    val ownerPath: List<String>,
    val name: String,
    val visibility: String,
    val typeParameters: List<SourceTypeParameter>,
    val underlyingType: SourceType,
    val expandedType: SourceType,
    val annotations: List<SourceAnnotation>
) : BirValue {
    override fun writeJson(output: StringBuilder) = SourceMetadataJson.write(this, output)
}

data class SourceClassifier(
    val packageName: String,
    val ownerPath: List<String>,
    val name: String
)

data class SourceTypeParameter(
    val id: Int,
    val name: String,
    val variance: String,
    val bounds: List<SourceType>,
    val annotations: List<SourceAnnotation>
)

sealed interface SourceType {
    data class ClassType(
        val classifier: SourceClassifier,
        val nullable: Boolean,
        val suspend: Boolean,
        val definitelyNotNull: Boolean,
        val arguments: List<SourceTypeArgument>,
        val annotations: List<SourceAnnotation>,
        val outerType: SourceType? = null,
        val abbreviatedType: SourceType? = null,
        val flexibleUpperBound: SourceType? = null,
        val flexibleCapabilities: String? = null
    ) : SourceType
    data class AliasType(
        val classifier: SourceClassifier,
        val nullable: Boolean,
        val suspend: Boolean,
        val definitelyNotNull: Boolean,
        val arguments: List<SourceTypeArgument>,
        val annotations: List<SourceAnnotation>,
        val outerType: SourceType? = null,
        val abbreviatedType: SourceType? = null,
        val flexibleUpperBound: SourceType? = null,
        val flexibleCapabilities: String? = null
    ) : SourceType
    data class ParameterType(
        val id: Int,
        val nullable: Boolean,
        val suspend: Boolean,
        val definitelyNotNull: Boolean,
        val arguments: List<SourceTypeArgument>,
        val annotations: List<SourceAnnotation>,
        val outerType: SourceType? = null,
        val abbreviatedType: SourceType? = null,
        val flexibleUpperBound: SourceType? = null,
        val flexibleCapabilities: String? = null
    ) : SourceType
}

sealed interface SourceTypeArgument {
    data class InvArgument(
        val type: SourceType
    ) : SourceTypeArgument
    data class InArgument(
        val type: SourceType
    ) : SourceTypeArgument
    data class OutArgument(
        val type: SourceType
    ) : SourceTypeArgument
    data object StarArgument : SourceTypeArgument
}

data class SourceAnnotation(
    val classifier: SourceClassifier,
    val arguments: List<SourceAnnotationArgument>
)

data class SourceAnnotationArgument(
    val name: String,
    val value: SourceAnnotationValue
)

sealed interface SourceAnnotationValue {
    data class ByteValue(
        val unsigned: Boolean,
        val value: Long
    ) : SourceAnnotationValue
    data class CharValue(
        val unsigned: Boolean,
        val value: Long
    ) : SourceAnnotationValue
    data class ShortValue(
        val unsigned: Boolean,
        val value: Long
    ) : SourceAnnotationValue
    data class IntValue(
        val unsigned: Boolean,
        val value: Long
    ) : SourceAnnotationValue
    data class LongValue(
        val unsigned: Boolean,
        val value: Long
    ) : SourceAnnotationValue
    data class BooleanValue(
        val unsigned: Boolean,
        val value: Long
    ) : SourceAnnotationValue
    data class StringValue(
        val unsigned: Boolean,
        val value: String
    ) : SourceAnnotationValue
    data class FloatValue(
        val unsigned: Boolean,
        val bits: Long
    ) : SourceAnnotationValue
    data class DoubleValue(
        val unsigned: Boolean,
        val bits: Long
    ) : SourceAnnotationValue
    data class ClassValue(
        val unsigned: Boolean,
        val classifier: SourceClassifier,
        val arrayDimensions: Int
    ) : SourceAnnotationValue
    data class EnumValue(
        val unsigned: Boolean,
        val classifier: SourceClassifier,
        val name: String
    ) : SourceAnnotationValue
    data class AnnotationValue(
        val unsigned: Boolean,
        val value: SourceAnnotation
    ) : SourceAnnotationValue
    data class ArrayValue(
        val unsigned: Boolean,
        val value: List<SourceAnnotationValue>
    ) : SourceAnnotationValue
}
