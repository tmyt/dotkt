package kotc.bir

import kotc.backend.TypeAliasDeclarations
import org.jetbrains.kotlin.metadata.ProtoBuf
import org.jetbrains.kotlin.metadata.deserialization.Flags
import org.jetbrains.kotlin.metadata.deserialization.NameResolverImpl
import org.jetbrains.kotlin.metadata.deserialization.TypeTable
import kotlin.test.Test
import kotlin.test.assertEquals

class SourceMetadataTest {
    private val text = "quote\"\\\n\u0001κ😀"
    private val floatBits = 0xffc00017L.toInt()

    @Test
    fun typedSourceDeclarationsKeepEveryTypeProjectionAndAnnotationConstant() {
        val classifier = SourceClassifier("sample", listOf("Outer"), "Inner")
        val parameter = SourceType.ParameterType(0, false, false, false, emptyList(), emptyList())
        val alias = SourceType.AliasType(classifier, true, true, false, emptyList(), emptyList())
        val arguments = listOf(SourceTypeArgument.InvArgument(parameter), SourceTypeArgument.InArgument(alias),
            SourceTypeArgument.OutArgument(parameter), SourceTypeArgument.StarArgument)
        val plain = SourceType.ClassType(classifier, false, false, false, arguments, emptyList())
        val expanded = plain.copy(outerType = parameter, abbreviatedType = alias,
            flexibleUpperBound = plain, flexibleCapabilities = "kotlin.jvm.PlatformType")
        val nested = SourceAnnotation(SourceClassifier("sample", emptyList(), "Nested"), listOf(SourceAnnotationArgument("text",
            SourceAnnotationValue.StringValue(false, "nested"))))
        val values = listOf(
            SourceAnnotationValue.ByteValue(false, -128),
            SourceAnnotationValue.CharValue(false, 0xd800),
            SourceAnnotationValue.ShortValue(false, -32768),
            SourceAnnotationValue.IntValue(false, Int.MIN_VALUE.toLong()),
            SourceAnnotationValue.LongValue(true, -1),
            SourceAnnotationValue.BooleanValue(false, 1),
            SourceAnnotationValue.StringValue(false, text),
            SourceAnnotationValue.FloatValue(false, floatBits.toLong()),
            SourceAnnotationValue.DoubleValue(false, Long.MIN_VALUE),
            SourceAnnotationValue.ClassValue(false, classifier, 2),
            SourceAnnotationValue.EnumValue(false, SourceClassifier("sample", emptyList(), "Values"), "ENTRY"),
            SourceAnnotationValue.AnnotationValue(false, nested),
            SourceAnnotationValue.ArrayValue(false, listOf(SourceAnnotationValue.IntValue(false, 42))),
        )
        val annotation = projectedAnnotation()
        assertEquals(SourceClassifier("sample", emptyList(), "Mark"), annotation.classifier)
        assertEquals(values, annotation.arguments.map { it.value })
        val declaration = SourceTypeAlias("sample", listOf("Owner"), "Alias", "public",
            listOf(SourceTypeParameter(0, "T", "out", listOf(plain), emptyList())),
            alias, expanded, listOf(annotation))
        val encoded = SourceMetadataJson.encode(declaration)
        assertEquals(encoded, SourceMetadataJson.encode(declaration))
        println("IR_SOURCE_METADATA_SAMPLE $encoded")
    }

    private fun projectedAnnotation(): SourceAnnotation {
        val strings = ProtoBuf.StringTable.newBuilder().addAllString(
            listOf("sample", "Outer", "Inner", "Values", "Nested", "Mark", "Alias", "text", "nested", "ENTRY", text)
                + (0..12).map { "a$it" }).build()
        fun qualified(shortName: Int, parent: Int,
            kind: ProtoBuf.QualifiedNameTable.QualifiedName.Kind = ProtoBuf.QualifiedNameTable.QualifiedName.Kind.CLASS
        ) = ProtoBuf.QualifiedNameTable.QualifiedName.newBuilder()
            .setShortName(shortName).setParentQualifiedName(parent).setKind(kind).build()
        val qualifiedNames = ProtoBuf.QualifiedNameTable.newBuilder().addAllQualifiedName(listOf(
            qualified(0, -1, ProtoBuf.QualifiedNameTable.QualifiedName.Kind.PACKAGE), qualified(1, 0),
            qualified(2, 1), qualified(3, 0), qualified(4, 0), qualified(5, 0))).build()
        fun value(kind: ProtoBuf.Annotation.Argument.Value.Type) = ProtoBuf.Annotation.Argument.Value.newBuilder().setType(kind)
        val nested = ProtoBuf.Annotation.newBuilder().setId(4).addArgument(
            ProtoBuf.Annotation.Argument.newBuilder().setNameId(7).setValue(
                value(ProtoBuf.Annotation.Argument.Value.Type.STRING).setStringValue(8))).build()
        val values = listOf(
            value(ProtoBuf.Annotation.Argument.Value.Type.BYTE).setIntValue(-128),
            value(ProtoBuf.Annotation.Argument.Value.Type.CHAR).setIntValue(0xd800),
            value(ProtoBuf.Annotation.Argument.Value.Type.SHORT).setIntValue(-32768),
            value(ProtoBuf.Annotation.Argument.Value.Type.INT).setIntValue(Int.MIN_VALUE.toLong()),
            value(ProtoBuf.Annotation.Argument.Value.Type.LONG).setIntValue(-1).setFlags(Flags.IS_UNSIGNED.toFlags(true)),
            value(ProtoBuf.Annotation.Argument.Value.Type.BOOLEAN).setIntValue(1),
            value(ProtoBuf.Annotation.Argument.Value.Type.STRING).setStringValue(10),
            value(ProtoBuf.Annotation.Argument.Value.Type.FLOAT).setFloatValue(Float.fromBits(floatBits)),
            value(ProtoBuf.Annotation.Argument.Value.Type.DOUBLE).setDoubleValue(Double.fromBits(Long.MIN_VALUE)),
            value(ProtoBuf.Annotation.Argument.Value.Type.CLASS).setClassId(2).setArrayDimensionCount(2),
            value(ProtoBuf.Annotation.Argument.Value.Type.ENUM).setClassId(3).setEnumValueId(9),
            value(ProtoBuf.Annotation.Argument.Value.Type.ANNOTATION).setAnnotation(nested),
            value(ProtoBuf.Annotation.Argument.Value.Type.ARRAY).addArrayElement(
                value(ProtoBuf.Annotation.Argument.Value.Type.INT).setIntValue(42)),
        )
        val sourceType = ProtoBuf.Type.newBuilder().setClassName(2).build()
        val alias = ProtoBuf.TypeAlias.newBuilder().setName(6)
            .setFlags(Flags.getTypeAliasFlags(true, ProtoBuf.Visibility.PUBLIC))
            .setUnderlyingType(sourceType).setExpandedType(sourceType)
            .addAnnotation(ProtoBuf.Annotation.newBuilder().setId(5).addAllArgument(values.mapIndexed { index, value ->
                ProtoBuf.Annotation.Argument.newBuilder().setNameId(11 + index).setValue(value).build()
            })).build()
        return TypeAliasDeclarations.Projection(NameResolverImpl(strings, qualifiedNames))
            .declaration("sample", emptyList(), alias, TypeTable(ProtoBuf.TypeTable.getDefaultInstance()))
            .annotations.single()
    }
}
