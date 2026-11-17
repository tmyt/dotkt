/*
 * Metadata glue adapted from Kotlin 2.4.21's MetadataLibraryBasedSymbolProvider.
 * Copyright 2010-2026 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Apache-2.0: https://github.com/JetBrains/kotlin/blob/v2.4.21/license/LICENSE.txt
 */
@file:OptIn(org.jetbrains.kotlin.fir.symbols.SymbolInternals::class)

package kotc.pipeline

import org.jetbrains.kotlin.config.AnalysisFlags
import org.jetbrains.kotlin.descriptors.annotations.AnnotationUseSiteTarget
import org.jetbrains.kotlin.fir.FirModuleData
import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.fir.declarations.FirDeclarationOrigin
import org.jetbrains.kotlin.fir.declarations.FirFunction
import org.jetbrains.kotlin.fir.declarations.FirProperty
import org.jetbrains.kotlin.fir.declarations.utils.klibFileAnnotations
import org.jetbrains.kotlin.fir.declarations.utils.klibSourceFile
import org.jetbrains.kotlin.fir.deserialization.*
import org.jetbrains.kotlin.fir.isNewPlaceForBodyGeneration
import org.jetbrains.kotlin.fir.languageVersionSettings
import org.jetbrains.kotlin.fir.resolve.providers.FirSymbolProvider
import org.jetbrains.kotlin.fir.scopes.FirKotlinScopeProvider
import org.jetbrains.kotlin.fir.session.KlibBasedAnnotationDeserializer
import org.jetbrains.kotlin.library.KotlinLibrary
import org.jetbrains.kotlin.library.components.metadata
import org.jetbrains.kotlin.library.metadata.*
import org.jetbrains.kotlin.metadata.ProtoBuf
import org.jetbrains.kotlin.metadata.deserialization.NameResolverImpl
import org.jetbrains.kotlin.metadata.deserialization.getExtensionOrNull
import org.jetbrains.kotlin.name.ClassId
import org.jetbrains.kotlin.name.FqName
import org.jetbrains.kotlin.protobuf.GeneratedMessageLite
import org.jetbrains.kotlin.resolve.CommonCompilerDeserializationConfiguration
import org.jetbrains.kotlin.serialization.deserialization.getClassId
import org.jetbrains.kotlin.util.toMetadataVersion
import java.util.Base64

private const val STRING_TABLE_CODEC = "dotkt_string_table_codec"
private const val UTF16_STRING_TABLE_CODEC = "utf16le-base64"

internal class ClrKlibStringTables(val libraries: List<KotlinLibrary>) {
    private val encoded = libraries.filter { it.manifestProperties.containsKey(STRING_TABLE_CODEC) }
    // One provider retains the original library order even when only one table needs this codec.
    val standard = if (encoded.isEmpty()) libraries else emptyList()

    fun providers(session: FirSession, modules: ModuleDataProvider, scopes: FirKotlinScopeProvider): List<FirSymbolProvider> =
        if (encoded.isEmpty()) emptyList() else listOf(ClrUtf16KlibSymbolProvider(session, modules, scopes, libraries))
}

/** Decode at the metadata boundary, before any declaration/annotation is deserialized. */
internal fun clrKlibNameResolver(library: KotlinLibrary, fragment: ProtoBuf.PackageFragment): NameResolverImpl {
    val codec = library.manifestProperties.getProperty(STRING_TABLE_CODEC)
        ?: return NameResolverImpl(fragment.strings, fragment.qualifiedNames)
    require(codec == UTF16_STRING_TABLE_CODEC) { "Unsupported KLIB string table codec: $codec" }
    val strings = ProtoBuf.StringTable.newBuilder().addAllString(fragment.strings.stringList.map { encoded ->
        val bytes = Base64.getDecoder().decode(encoded)
        require(bytes.size % 2 == 0) { "Malformed UTF-16 KLIB string" }
        CharArray(bytes.size / 2) { index ->
            ((bytes[index * 2].toInt() and 255) or ((bytes[index * 2 + 1].toInt() and 255) shl 8)).toChar()
        }.concatToString()
    }).build()
    return NameResolverImpl(strings, fragment.qualifiedNames)
}

/** The stock public declaration deserializers, with a codec-selected NameResolver.
 * Session/metadata glue follows Kotlin 2.4.21's MetadataLibraryBasedSymbolProvider;
 * no overload, type, annotation or constant semantics are reimplemented here. */
private class ClrUtf16KlibSymbolProvider(
    session: FirSession,
    modules: ModuleDataProvider,
    scopes: FirKotlinScopeProvider,
    libraries: List<KotlinLibrary>,
) : AbstractFirDeserializedSymbolProvider(
    session, modules, scopes, FirDeclarationOrigin.Library, KlibMetadataSerializerProtocol,
) {
    private val annotationDeserializer = KlibBasedAnnotationDeserializer(session)
    private val constDeserializer = FirConstDeserializer(KlibMetadataSerializerProtocol)
    private val flexibleTypes = FirTypeDeserializer.FlexibleTypeFactory.Default
    private val configuration = CommonCompilerDeserializationConfiguration(session.languageVersionSettings)
    private val headers = libraries.associateWith { parseModuleHeader(it.metadata.moduleHeaderData) }
    private val librariesByPackage = buildMap<String, MutableList<KotlinLibrary>> {
        for ((library, header) in headers)
            for (name in header.packageFragmentNameList) getOrPut(name) { mutableListOf() }.add(library)
    }
    private class Part(
        val library: KotlinLibrary,
        val fragment: ProtoBuf.PackageFragment,
        val names: NameResolverImpl,
        val module: FirModuleData,
        val source: KlibDeserializedContainerSource,
    ) : PackagePartsCacheData.Extra {
        val finder = KlibMetadataClassDataFinder(fragment, names)
    }
    private val parts = mutableMapOf<FqName, List<Part>>()

    private fun parts(packageName: FqName): List<Part> = parts.getOrPut(packageName) {
        librariesByPackage[packageName.asString()].orEmpty().flatMap { library ->
            val module = moduleDataProvider.getModuleData(library.path) ?: return@flatMap emptyList()
            val incompatibility = if (session.languageVersionSettings.getFlag(AnalysisFlags.skipMetadataVersionCheck)) null
                else library.getIncompatibility(session.languageVersionSettings.languageVersion.toMetadataVersion())
            val source = KlibDeserializedContainerSource(library, headers.getValue(library), configuration, packageName, incompatibility)
            library.metadata.getPackageFragmentNames(packageName.asString()).map { name ->
                val fragment = parsePackageFragment(library.metadata.getPackageFragment(packageName.asString(), name))
                Part(library, fragment, clrKlibNameResolver(library, fragment), module, source)
            }
        }
    }

    override fun computePackagePartsInfos(packageFqName: FqName) = parts(packageFqName).map { part ->
        PackagePartsCacheData(part.fragment.`package`, FirDeserializationContext.createForPackage(
            packageFqName, part.fragment.`package`, part.names, part.module,
            annotationDeserializer, flexibleTypes, constDeserializer, kdocDeserializer, part.source,
        ), part, fileAnnotations = fileAnnotations(part))
    }

    override fun computePackageSetWithNonClassDeclarations() = librariesByPackage.keys

    override fun knownTopLevelClassesInPackage(packageFqName: FqName) = buildSet {
        for (part in parts(packageFqName))
            for (index in part.fragment.getExtension(KlibMetadataProtoBuf.className).orEmpty())
                add(part.names.getClassId(index).shortClassName.asString())
    }

    override fun extractClassMetadata(classId: ClassId, parentContext: FirDeserializationContext?): ClassMetadataFindResult? {
        for (part in parts(classId.packageFqName)) {
            val proto = part.finder.findClassData(classId)?.classProto ?: continue
            return ClassMetadataFindResult.NoMetadata { symbol ->
                deserializeClassToSymbol(classId, proto, symbol, part.names, session, part.module,
                    annotationDeserializer, kdocDeserializer, flexibleTypes, kotlinScopeProvider,
                    KlibMetadataSerializerProtocol, parentContext, part.source,
                    origin = defaultDeserializationOrigin, deserializeNestedClass = this::getClass,
                    deserializeNestedTypeAlias = this::getTypeAlias)
                symbol.fir.klibSourceFile = sourceFile(part, proto, KlibMetadataProtoBuf.classFile)
                symbol.fir.isNewPlaceForBodyGeneration = false
                val annotations = fileAnnotations(part)
                if (annotations.isNotEmpty()) symbol.fir.klibFileAnnotations = annotations
            }
        }
        return null
    }

    private fun fileAnnotations(part: Part) = loadAnnotationsFromMetadata(
        session, part.fragment.fileAnnotationList, part.names, AnnotationUseSiteTarget.FILE)

    override fun loadFunctionExtensions(packagePart: PackagePartsCacheData, proto: ProtoBuf.Function, fir: FirFunction) {
        fir.klibSourceFile = sourceFile(packagePart.extra as Part, proto, KlibMetadataProtoBuf.functionFile) ?: return
    }

    override fun loadPropertyExtensions(packagePart: PackagePartsCacheData, proto: ProtoBuf.Property, fir: FirProperty) {
        fir.klibSourceFile = sourceFile(packagePart.extra as Part, proto, KlibMetadataProtoBuf.propertyFile) ?: return
    }

    private fun <T : GeneratedMessageLite.ExtendableMessage<T>> sourceFile(
        part: Part, proto: T, extension: GeneratedMessageLite.GeneratedExtension<T, Int>,
    ) = proto.getExtensionOrNull(extension)?.let { DeserializedSourceFile(part.names.getString(it), part.library) }

    override fun isNewPlaceForBodyGeneration(classProto: ProtoBuf.Class) = false

    override fun hasPackage(fqName: FqName) = fqName.asString() in librariesByPackage ||
        librariesByPackage.keys.any { it.startsWith(fqName.asString() + ".") }
}
