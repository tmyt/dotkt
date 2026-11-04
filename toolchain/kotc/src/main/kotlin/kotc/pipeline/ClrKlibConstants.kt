@file:OptIn(org.jetbrains.kotlin.fir.symbols.SymbolInternals::class)

package kotc.pipeline

import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.fir.SessionConfiguration
import org.jetbrains.kotlin.fir.deserialization.FirConstDeserializer
import org.jetbrains.kotlin.fir.deserialization.FirDeserializationExtension
import org.jetbrains.kotlin.fir.deserialization.ModuleDataProvider
import org.jetbrains.kotlin.fir.expressions.FirExpression
import org.jetbrains.kotlin.fir.expressions.builder.buildLiteralExpression
import org.jetbrains.kotlin.fir.resolve.providers.symbolProvider
import org.jetbrains.kotlin.library.KotlinLibrary
import org.jetbrains.kotlin.library.components.metadata
import org.jetbrains.kotlin.library.metadata.KlibDeserializedContainerSource
import org.jetbrains.kotlin.library.metadata.KlibMetadataSerializerProtocol
import org.jetbrains.kotlin.library.metadata.parseModuleHeader
import org.jetbrains.kotlin.library.metadata.parsePackageFragment
import org.jetbrains.kotlin.metadata.ProtoBuf
import org.jetbrains.kotlin.metadata.ProtoBuf.Annotation.Argument.Value
import org.jetbrains.kotlin.metadata.deserialization.Flags
import org.jetbrains.kotlin.metadata.deserialization.NameResolver
import org.jetbrains.kotlin.metadata.deserialization.NameResolverImpl
import org.jetbrains.kotlin.metadata.deserialization.getExtensionOrNull
import org.jetbrains.kotlin.name.CallableId
import org.jetbrains.kotlin.name.FqName
import org.jetbrains.kotlin.name.Name
import org.jetbrains.kotlin.serialization.SerializerExtensionProtocol
import org.jetbrains.kotlin.serialization.deserialization.descriptors.DeserializedContainerSource
import org.jetbrains.kotlin.types.ConstantValueKind

private const val UTF16_CONSTANTS = "dotkt_utf16_constants"

internal fun prepareClrKlibConstants(libraries: List<KotlinLibrary>, moduleDataProvider: ModuleDataProvider) {
	val encodedLibraries = libraries.filter { it.manifestProperties.containsKey(UTF16_CONSTANTS) }
	if (encodedLibraries.isEmpty()) return
	val librarySessions = moduleDataProvider.allModuleData.map { it.session }.distinct()
	librarySessions.forEach(::installClrKlibConstantDeserialization)

	// Upstream's KLIB provider uses the extension for class members but constructs a stock
	// constant deserializer for package members. Restore only the explicit package constants
	// on those already-deserialized symbols, before source resolution can evaluate them.
	val protocol = KlibMetadataSerializerProtocol
	for (library in encodedLibraries) {
		requireUtf16Version(library)
		val metadata = library.metadata
		for (packageName in parseModuleHeader(metadata.moduleHeaderData).packageFragmentNameList) {
			val packageFqName = FqName(packageName)
			for (part in metadata.getPackageFragmentNames(packageName)) {
				val fragment = parsePackageFragment(metadata.getPackageFragment(packageName, part))
				val names = NameResolverImpl(fragment.strings, fragment.qualifiedNames)
				for (property in fragment.`package`.propertyList) {
					val encoded = property.getExtensionOrNull(protocol.compileTimeValue)
					if (encoded?.type != Value.Type.ARRAY) continue
					val name = Name.identifier(names.getString(property.name))
					val id = CallableId(packageFqName, name)
					val literal = decodeUtf16Constant(property, id, names, encoded)
					val declarations = librarySessions.flatMap { it.symbolProvider.getTopLevelPropertySymbols(packageFqName, name) }
						.distinct().map { it.fir }.filter {
							(it.containerSource as? KlibDeserializedContainerSource)?.klib == library
						}
					require(declarations.size == 1) { "UTF-16 constant $id selects ${declarations.size} declarations in its KLIB" }
					declarations.single().replaceInitializer(literal)
				}
			}
		}
	}
}

private fun requireUtf16Version(klib: KotlinLibrary) {
	val version = klib.manifestProperties.getProperty(UTF16_CONSTANTS)
	require(version == "1") { "Unknown DotKt KLIB UTF-16 constant version: $version" }
}

@OptIn(SessionConfiguration::class)
private fun installClrKlibConstantDeserialization(session: FirSession) {
	session.register(FirDeserializationExtension::class, object : FirDeserializationExtension(session) {
		override fun createConstDeserializer(
			containerSource: DeserializedContainerSource?,
			session: FirSession,
			serializerExtensionProtocol: SerializerExtensionProtocol,
		) = clrKlibConstantDeserializer(containerSource, serializerExtensionProtocol)
	})
}

private fun clrKlibConstantDeserializer(
	containerSource: DeserializedContainerSource?,
	protocol: SerializerExtensionProtocol,
): FirConstDeserializer? {
	val klib = (containerSource as? KlibDeserializedContainerSource)?.klib ?: return null
	if (!klib.manifestProperties.containsKey(UTF16_CONSTANTS)) return null
	requireUtf16Version(klib)
	return object : FirConstDeserializer(protocol) {
		override fun loadConstant(
			propertyProto: ProtoBuf.Property,
			callableId: CallableId,
			nameResolver: NameResolver,
			isUnsigned: Boolean,
		): FirExpression? {
			val encoded = propertyProto.getExtensionOrNull(protocol.compileTimeValue)
			if (encoded?.type != Value.Type.ARRAY)
				return super.loadConstant(propertyProto, callableId, nameResolver, isUnsigned)
			return decodeUtf16Constant(propertyProto, callableId, nameResolver, encoded)
		}
	}
}

private fun decodeUtf16Constant(
	property: ProtoBuf.Property,
	callableId: CallableId,
	names: NameResolver,
	encoded: Value,
): FirExpression {
	require(Flags.IS_CONST.get(property.flags) && Flags.HAS_CONSTANT.get(property.flags) &&
		property.hasReturnType() && property.returnType.hasClassName() &&
		names.getQualifiedClassName(property.returnType.className) == "kotlin/String" &&
		encoded.arrayElementCount > 0) { "Malformed UTF-16 String constant for $callableId" }
	val units = CharArray(encoded.arrayElementCount) { index ->
		val unit = encoded.getArrayElement(index)
		require(unit.type == Value.Type.CHAR && unit.hasIntValue() && unit.intValue in 0L..0xffffL) {
			"Malformed UTF-16 code unit for $callableId"
		}
		unit.intValue.toInt().toChar()
	}
	return buildLiteralExpression(null, ConstantValueKind.String, units.concatToString(), setType = true)
}
