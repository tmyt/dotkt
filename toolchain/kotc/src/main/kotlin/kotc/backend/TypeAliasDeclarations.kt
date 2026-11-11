@file:OptIn(org.jetbrains.kotlin.fir.declarations.DirectDeclarationsAccess::class)

package kotc.backend

import kotc.bir.SourceAnnotation
import kotc.bir.SourceAnnotationArgument
import kotc.bir.SourceAnnotationValue
import kotc.bir.SourceClassifier
import kotc.bir.SourceType
import kotc.bir.SourceTypeAlias
import kotc.bir.SourceTypeArgument
import kotc.bir.SourceTypeParameter
import org.jetbrains.kotlin.cli.pipeline.metadata.MetadataFrontendPipelineArtifact
import org.jetbrains.kotlin.fir.pipeline.Fir2IrActualizedResult
import org.jetbrains.kotlin.fir.pipeline.Fir2KlibMetadataSerializer
import org.jetbrains.kotlin.fir.declarations.FirDeclaration
import org.jetbrains.kotlin.fir.declarations.FirRegularClass
import org.jetbrains.kotlin.fir.declarations.FirTypeAlias
import org.jetbrains.kotlin.library.metadata.KlibMetadataProtoBuf
import org.jetbrains.kotlin.metadata.ProtoBuf
import org.jetbrains.kotlin.metadata.deserialization.Flags
import org.jetbrains.kotlin.metadata.deserialization.NameResolverImpl
import org.jetbrains.kotlin.metadata.deserialization.TypeTable
import java.io.File

/** Source-owned alias declarations, not executable types or CLR generic frames. The actualization-aware
 * frontend serializer is the authority for underlying/expanded types, annotations, and parameter identities.
 * Its resolved declarations become typed source metadata; JSON is written only at the file boundary.
 * No protobuf or KLIB is transported. */
internal object TypeAliasDeclarations {
    fun collect(frontend: MetadataFrontendPipelineArtifact, result: Fir2IrActualizedResult): Map<String, List<SourceTypeAlias>> {
        fun hasAliases(declarations: List<FirDeclaration>): Boolean = declarations.any {
            it is FirTypeAlias || it is FirRegularClass && hasAliases(it.declarations)
        }
        // Nested aliases need not survive Fir2Ir as IrTypeAlias children. Their resolved FIR declaration, rather
        // than executable IR containment, is the authority for whether a file owns declaration metadata.
        val sources = result.irModuleFragment.files.map { File(it.fileEntry.name).canonicalPath }.toSet()
        if (sources.isEmpty()) return emptyMap()
        val serializer = Fir2KlibMetadataSerializer(
            frontend.configuration, frontend.frontendOutput.outputs, result, produceHeaderKlib = false,
        )
        val declarations = linkedMapOf<String, List<SourceTypeAlias>>()
        serializer.forEachFile { _, source, firFile, _, packageName ->
            val path = source.canonicalPath
            if (path in sources && hasAliases(firFile.declarations)) {
                val fragment = serializer.serializeSingleFileMetadata(firFile)
                val projection = Projection(NameResolverImpl(fragment.strings, fragment.qualifiedNames))
                val aliases = mutableListOf<SourceTypeAlias>()
                val packageTypes = TypeTable(fragment.`package`.typeTable)
                for (alias in fragment.`package`.typeAliasList)
                    aliases += projection.declaration(packageName.asString(), emptyList<String>(), alias, packageTypes)
                for (owner in fragment.class_List) {
                    val identity = projection.classifier(owner.fqName)
                    val ownerPath = identity.ownerPath + identity.name
                    val types = TypeTable(owner.typeTable)
                    for (alias in owner.typeAliasList)
                        aliases += projection.declaration(packageName.asString(), ownerPath, alias, types)
                }
                if (aliases.isNotEmpty()) declarations[path] = aliases
            }
        }
        return declarations
    }

    internal class Projection(private val names: NameResolverImpl) {
        private var parameterIds: Map<String, Int> = emptyMap()
        fun classifier(index: Int): SourceClassifier {
            check(!names.isLocalClassName(index)) { "local classifier in typealias declaration metadata" }
            val packages = mutableListOf<String>()
            val path = mutableListOf<String>()
            var current = index
            while (current >= 0) {
                val entry = names.qualifiedNames.getQualifiedName(current)
                val name = names.getString(entry.shortName)
                when (entry.kind) {
                    ProtoBuf.QualifiedNameTable.QualifiedName.Kind.PACKAGE -> packages += name
                    ProtoBuf.QualifiedNameTable.QualifiedName.Kind.CLASS -> path += name
                    else -> error("local classifier in typealias declaration metadata")
                }
                current = entry.parentQualifiedName
            }
            path.reverse()
            return SourceClassifier(packages.asReversed().joinToString("."), path.dropLast(1), path.last())
        }

        fun declaration(packageName: String, ownerPath: List<String>, alias: ProtoBuf.TypeAlias, types: TypeTable): SourceTypeAlias {
            // Upstream metadata names alias-owned parameters in some underlying type slots. Resolve that explicit
            // frontend reference in this declaration's parameter table, never in a CLR owner/method frame.
            parameterIds = alias.typeParameterList.associate { names.getString(it.name) to it.id }
            return SourceTypeAlias(
                packageName = packageName,
                ownerPath = ownerPath,
                name = names.getString(alias.name),
                visibility = Flags.VISIBILITY.get(alias.flags).name.lowercase(),
                typeParameters = alias.typeParameterList.map { parameter ->
                    SourceTypeParameter(
                        id = parameter.id,
                        name = names.getString(parameter.name),
                        variance = parameter.variance.name.lowercase(),
                        bounds = (parameter.upperBoundList + parameter.upperBoundIdList.map { types[it] })
                            .map { type(it, types) },
                        annotations = (0 until parameter.getExtensionCount(KlibMetadataProtoBuf.typeParameterAnnotation))
                            .map { annotation(parameter.getExtension(KlibMetadataProtoBuf.typeParameterAnnotation, it)) },
                    )
                },
                underlyingType = type(if (alias.hasUnderlyingType()) alias.underlyingType else types[alias.underlyingTypeId], types),
                expandedType = type(if (alias.hasExpandedType()) alias.expandedType else types[alias.expandedTypeId], types),
                annotations = alias.annotationList.map(::annotation),
            )
        }

        private fun type(source: ProtoBuf.Type, types: TypeTable): SourceType {
            val nullable = source.nullable
            val suspend = Flags.SUSPEND_TYPE.get(source.flags)
            val definitelyNotNull = Flags.DEFINITELY_NOT_NULL_TYPE.get(source.flags)
            val arguments = source.argumentList.map { argument ->
                if (argument.projection == ProtoBuf.Type.Argument.Projection.STAR) SourceTypeArgument.StarArgument
                else {
                    val value = type(if (argument.hasType()) argument.type else types[argument.typeId], types)
                    when (argument.projection) {
                        ProtoBuf.Type.Argument.Projection.INV -> SourceTypeArgument.InvArgument(value)
                        ProtoBuf.Type.Argument.Projection.IN -> SourceTypeArgument.InArgument(value)
                        ProtoBuf.Type.Argument.Projection.OUT -> SourceTypeArgument.OutArgument(value)
                        else -> error("unrecognized Kotlin type projection")
                    }
                }
            }
            val annotations = (0 until source.getExtensionCount(KlibMetadataProtoBuf.typeAnnotation))
                .map { annotation(source.getExtension(KlibMetadataProtoBuf.typeAnnotation, it)) }
            val outerType = if (source.hasOuterType() || source.hasOuterTypeId())
                type(if (source.hasOuterType()) source.outerType else types[source.outerTypeId], types) else null
            val abbreviatedType = if (source.hasAbbreviatedType() || source.hasAbbreviatedTypeId())
                type(if (source.hasAbbreviatedType()) source.abbreviatedType else types[source.abbreviatedTypeId], types) else null
            val flexibleUpperBound = if (source.hasFlexibleUpperBound() || source.hasFlexibleUpperBoundId())
                type(if (source.hasFlexibleUpperBound()) source.flexibleUpperBound else types[source.flexibleUpperBoundId], types) else null
            val flexibleCapabilities = if (flexibleUpperBound != null)
                names.getString(source.flexibleTypeCapabilitiesId) else null
            return when {
                source.hasClassName() -> SourceType.ClassType(classifier(source.className),
                    nullable, suspend, definitelyNotNull, arguments, annotations,
                    outerType, abbreviatedType, flexibleUpperBound, flexibleCapabilities)
                source.hasTypeAliasName() -> SourceType.AliasType(classifier(source.typeAliasName),
                    nullable, suspend, definitelyNotNull, arguments, annotations,
                    outerType, abbreviatedType, flexibleUpperBound, flexibleCapabilities)
                source.hasTypeParameter() || source.hasTypeParameterName() -> SourceType.ParameterType(
                    if (source.hasTypeParameter()) source.typeParameter
                    else parameterIds.getValue(names.getString(source.typeParameterName)),
                    nullable, suspend, definitelyNotNull, arguments, annotations,
                    outerType, abbreviatedType, flexibleUpperBound, flexibleCapabilities)
                else -> error("typealias metadata type has no resolved classifier or parameter identity")
            }
        }

        private fun annotation(source: ProtoBuf.Annotation): SourceAnnotation = SourceAnnotation(
            classifier(source.id), source.argumentList.map {
                SourceAnnotationArgument(names.getString(it.nameId), value(it.value))
            },
        )

        private fun value(source: ProtoBuf.Annotation.Argument.Value): SourceAnnotationValue {
            val unsigned = Flags.IS_UNSIGNED.get(source.flags)
            return when (source.type) {
                ProtoBuf.Annotation.Argument.Value.Type.BYTE -> SourceAnnotationValue.ByteValue(unsigned, source.intValue)
                ProtoBuf.Annotation.Argument.Value.Type.CHAR -> SourceAnnotationValue.CharValue(unsigned, source.intValue)
                ProtoBuf.Annotation.Argument.Value.Type.SHORT -> SourceAnnotationValue.ShortValue(unsigned, source.intValue)
                ProtoBuf.Annotation.Argument.Value.Type.INT -> SourceAnnotationValue.IntValue(unsigned, source.intValue)
                ProtoBuf.Annotation.Argument.Value.Type.LONG -> SourceAnnotationValue.LongValue(unsigned, source.intValue)
                ProtoBuf.Annotation.Argument.Value.Type.BOOLEAN -> SourceAnnotationValue.BooleanValue(unsigned, source.intValue)
                ProtoBuf.Annotation.Argument.Value.Type.FLOAT -> SourceAnnotationValue.FloatValue(unsigned, source.floatValue.toRawBits().toLong())
                ProtoBuf.Annotation.Argument.Value.Type.DOUBLE -> SourceAnnotationValue.DoubleValue(unsigned, source.doubleValue.toRawBits())
                ProtoBuf.Annotation.Argument.Value.Type.STRING -> SourceAnnotationValue.StringValue(unsigned, names.getString(source.stringValue))
                ProtoBuf.Annotation.Argument.Value.Type.CLASS -> SourceAnnotationValue.ClassValue(unsigned,
                    classifier(source.classId), source.arrayDimensionCount)
                ProtoBuf.Annotation.Argument.Value.Type.ENUM -> SourceAnnotationValue.EnumValue(unsigned,
                    classifier(source.classId), names.getString(source.enumValueId))
                ProtoBuf.Annotation.Argument.Value.Type.ANNOTATION -> SourceAnnotationValue.AnnotationValue(unsigned, annotation(source.annotation))
                ProtoBuf.Annotation.Argument.Value.Type.ARRAY -> SourceAnnotationValue.ArrayValue(unsigned, source.arrayElementList.map(::value))
                else -> error("unrecognized Kotlin annotation constant kind")
            }
        }
    }
}
