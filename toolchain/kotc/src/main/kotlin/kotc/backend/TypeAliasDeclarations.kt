@file:OptIn(org.jetbrains.kotlin.fir.declarations.DirectDeclarationsAccess::class)

package kotc.backend

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
 * Its resolved declaration objects are projected to structured JSON; no protobuf or KLIB is transported. */
internal object TypeAliasDeclarations {
    fun collect(frontend: MetadataFrontendPipelineArtifact, result: Fir2IrActualizedResult): Map<String, List<String>> {
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
        val declarations = linkedMapOf<String, List<String>>()
        serializer.forEachFile { _, source, firFile, _, packageName ->
            val path = source.canonicalPath
            if (path in sources && hasAliases(firFile.declarations)) {
                val fragment = serializer.serializeSingleFileMetadata(firFile)
                val projection = Projection(NameResolverImpl(fragment.strings, fragment.qualifiedNames))
                val aliases = mutableListOf<String>()
                val packageTypes = TypeTable(fragment.`package`.typeTable)
                for (alias in fragment.`package`.typeAliasList)
                    aliases += projection.declaration(packageName.asString(), emptyList<String>(), alias, packageTypes)
                for (owner in fragment.class_List) {
                    val identity = projection.classifier(owner.fqName)
                    val ownerPath = (identity.getValue("ownerPath") as List<*>) + identity.getValue("name")
                    val types = TypeTable(owner.typeTable)
                    for (alias in owner.typeAliasList)
                        aliases += projection.declaration(packageName.asString(), ownerPath, alias, types)
                }
                if (aliases.isNotEmpty()) declarations[path] = aliases
            }
        }
        return declarations
    }

    private class Projection(private val names: NameResolverImpl) {
        private var parameterIds: Map<String, Int> = emptyMap()
        fun classifier(index: Int): Map<String, Any> {
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
            return linkedMapOf("packageName" to packages.asReversed().joinToString("."),
                "ownerPath" to path.dropLast(1), "name" to path.last())
        }

        fun declaration(packageName: String, ownerPath: List<*>, alias: ProtoBuf.TypeAlias, types: TypeTable): String {
            // Upstream metadata names alias-owned parameters in some underlying type slots. Resolve that explicit
            // frontend reference in this declaration's parameter table, never in a CLR owner/method frame.
            parameterIds = alias.typeParameterList.associate { names.getString(it.name) to it.id }
            return json(
            linkedMapOf(
                "packageName" to packageName,
                "ownerPath" to ownerPath,
                "name" to names.getString(alias.name),
                "visibility" to Flags.VISIBILITY.get(alias.flags).name.lowercase(),
                "typeParameters" to alias.typeParameterList.map { parameter ->
                    linkedMapOf(
                        "id" to parameter.id,
                        "name" to names.getString(parameter.name),
                        "variance" to parameter.variance.name.lowercase(),
                        "bounds" to (parameter.upperBoundList + parameter.upperBoundIdList.map { types[it] })
                            .map { type(it, types) },
                    )
                },
                "underlyingType" to type(if (alias.hasUnderlyingType()) alias.underlyingType else types[alias.underlyingTypeId], types),
                "expandedType" to type(if (alias.hasExpandedType()) alias.expandedType else types[alias.expandedTypeId], types),
                "annotations" to alias.annotationList.map(::annotation),
            ),
            )
        }

        private fun type(source: ProtoBuf.Type, types: TypeTable): Map<String, Any> {
            val node = linkedMapOf<String, Any>()
            when {
                source.hasClassName() -> {
                    node["kind"] = "class"
                    node["classifier"] = classifier(source.className)
                }
                source.hasTypeAliasName() -> {
                    node["kind"] = "alias"
                    node["classifier"] = classifier(source.typeAliasName)
                }
                source.hasTypeParameter() -> {
                    node["kind"] = "parameter"
                    node["id"] = source.typeParameter
                }
                source.hasTypeParameterName() -> {
                    node["kind"] = "parameter"
                    node["id"] = parameterIds.getValue(names.getString(source.typeParameterName))
                }
                else -> error("typealias metadata type has no resolved classifier or parameter identity")
            }
            node["nullable"] = source.nullable
            node["suspend"] = Flags.SUSPEND_TYPE.get(source.flags)
            node["definitelyNotNull"] = Flags.DEFINITELY_NOT_NULL_TYPE.get(source.flags)
            node["arguments"] = source.argumentList.map { argument ->
                linkedMapOf<String, Any>("variance" to argument.projection.name.lowercase()).also {
                    if (argument.projection != ProtoBuf.Type.Argument.Projection.STAR)
                        it["type"] = type(if (argument.hasType()) argument.type else types[argument.typeId], types)
                }
            }
            node["annotations"] = (0 until source.getExtensionCount(KlibMetadataProtoBuf.typeAnnotation))
                .map { annotation(source.getExtension(KlibMetadataProtoBuf.typeAnnotation, it)) }
            if (source.hasOuterType() || source.hasOuterTypeId())
                node["outerType"] = type(if (source.hasOuterType()) source.outerType else types[source.outerTypeId], types)
            if (source.hasAbbreviatedType() || source.hasAbbreviatedTypeId())
                node["abbreviatedType"] = type(if (source.hasAbbreviatedType()) source.abbreviatedType else types[source.abbreviatedTypeId], types)
            if (source.hasFlexibleUpperBound() || source.hasFlexibleUpperBoundId()) {
                node["flexibleUpperBound"] = type(if (source.hasFlexibleUpperBound()) source.flexibleUpperBound else types[source.flexibleUpperBoundId], types)
                node["flexibleCapabilities"] = names.getString(source.flexibleTypeCapabilitiesId)
            }
            return node
        }

        private fun annotation(source: ProtoBuf.Annotation): Map<String, Any> = linkedMapOf(
            "classifier" to classifier(source.id),
            "arguments" to source.argumentList.map {
                linkedMapOf("name" to names.getString(it.nameId), "value" to value(it.value))
            },
        )

        private fun value(source: ProtoBuf.Annotation.Argument.Value): Map<String, Any> {
            val node = linkedMapOf<String, Any>("kind" to source.type.name.lowercase())
            when (source.type) {
                ProtoBuf.Annotation.Argument.Value.Type.BYTE,
                ProtoBuf.Annotation.Argument.Value.Type.CHAR,
                ProtoBuf.Annotation.Argument.Value.Type.SHORT,
                ProtoBuf.Annotation.Argument.Value.Type.INT,
                ProtoBuf.Annotation.Argument.Value.Type.LONG,
                ProtoBuf.Annotation.Argument.Value.Type.BOOLEAN -> node["value"] = source.intValue
                ProtoBuf.Annotation.Argument.Value.Type.FLOAT -> node["bits"] = source.floatValue.toRawBits()
                ProtoBuf.Annotation.Argument.Value.Type.DOUBLE -> node["bits"] = source.doubleValue.toRawBits()
                ProtoBuf.Annotation.Argument.Value.Type.STRING -> node["value"] = names.getString(source.stringValue)
                ProtoBuf.Annotation.Argument.Value.Type.CLASS -> {
                    node["classifier"] = classifier(source.classId)
                    node["arrayDimensions"] = source.arrayDimensionCount
                }
                ProtoBuf.Annotation.Argument.Value.Type.ENUM -> {
                    node["classifier"] = classifier(source.classId)
                    node["name"] = names.getString(source.enumValueId)
                }
                ProtoBuf.Annotation.Argument.Value.Type.ANNOTATION -> node["value"] = annotation(source.annotation)
                ProtoBuf.Annotation.Argument.Value.Type.ARRAY -> node["value"] = source.arrayElementList.map(::value)
                else -> error("unrecognized Kotlin annotation constant kind")
            }
            node["unsigned"] = Flags.IS_UNSIGNED.get(source.flags)
            return node
        }
    }

    private fun json(value: Any?): String = when (value) {
        null -> "null"
        is String -> buildString {
            append('"')
            for (character in value) when (character) {
                '"' -> append("\\\"")
                '\\' -> append("\\\\")
                in '\u0000'..'\u001f' -> append("\\u").append(character.code.toString(16).padStart(4, '0'))
                else -> append(character)
            }
            append('"')
        }
        is Map<*, *> -> value.entries.joinToString(",", "{", "}") { json(it.key) + ":" + json(it.value) }
        is List<*> -> value.joinToString(",", "[", "]", transform = ::json)
        is Number, is Boolean -> value.toString()
        else -> error("unsupported typealias JSON value ${value.javaClass.name}")
    }
}
