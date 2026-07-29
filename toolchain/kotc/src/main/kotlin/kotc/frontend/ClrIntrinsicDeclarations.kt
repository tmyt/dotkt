@file:OptIn(
	org.jetbrains.kotlin.fir.extensions.FirExtensionApiInternals::class,
	org.jetbrains.kotlin.fir.extensions.ExperimentalTopLevelDeclarationsGenerationApi::class,
	org.jetbrains.kotlin.compiler.plugin.ExperimentalCompilerApi::class,
)

package kotc.frontend

import org.jetbrains.kotlin.GeneratedDeclarationKey
import org.jetbrains.kotlin.compiler.plugin.CompilerPluginRegistrar
import org.jetbrains.kotlin.config.CompilerConfiguration
import org.jetbrains.kotlin.descriptors.ClassKind
import org.jetbrains.kotlin.descriptors.Modality
import org.jetbrains.kotlin.descriptors.Visibilities
import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.fir.extensions.FirDeclarationGenerationExtension
import org.jetbrains.kotlin.fir.extensions.FirExtensionRegistrar
import org.jetbrains.kotlin.fir.extensions.FirExtensionRegistrarAdapter
import org.jetbrains.kotlin.fir.extensions.MemberGenerationContext
import org.jetbrains.kotlin.fir.plugin.createConstructor
import org.jetbrains.kotlin.fir.plugin.createMemberFunction
import org.jetbrains.kotlin.fir.plugin.createMemberProperty
import org.jetbrains.kotlin.fir.plugin.createTopLevelClass
import org.jetbrains.kotlin.fir.plugin.createTopLevelFunction
import org.jetbrains.kotlin.fir.resolve.providers.symbolProvider
import org.jetbrains.kotlin.fir.symbols.impl.FirClassLikeSymbol
import org.jetbrains.kotlin.fir.symbols.impl.FirClassSymbol
import org.jetbrains.kotlin.fir.symbols.impl.FirConstructorSymbol
import org.jetbrains.kotlin.fir.symbols.impl.FirNamedFunctionSymbol
import org.jetbrains.kotlin.fir.symbols.impl.FirPropertySymbol
import org.jetbrains.kotlin.fir.types.ConeKotlinType
import org.jetbrains.kotlin.fir.types.coneType
import org.jetbrains.kotlin.fir.types.constructType
import org.jetbrains.kotlin.name.CallableId
import org.jetbrains.kotlin.name.ClassId
import org.jetbrains.kotlin.name.FqName
import org.jetbrains.kotlin.name.Name
import org.jetbrains.kotlin.name.SpecialNames

/** Marks declarations synthesized solely for Kotlin/CLR frontend resolution. */
object ClrGeneratedKey : GeneratedDeclarationKey()

/**
 * Declares the small, compiler-owned `kotlin.clr` vocabulary that has no KLIB declaration.
 *
 * CLR reference declarations come exclusively from dll2klib KLIBs. This extension must therefore
 * contain only compile-time intrinsics whose meaning is owned by kotc; it must not reconstruct CLR
 * types or Kotlin declarations from an out-of-band metadata registry.
 */
private class ClrIntrinsicDeclarationGenerator(session: FirSession) : FirDeclarationGenerationExtension(session) {
	private val clrPackage = FqName("kotlin.clr")

	private val byrefName = Name.identifier("byref")
	private val stackBufferName = Name.identifier("stackBuffer")
	private val clrEventName = Name.identifier("clrEvent")

	private val clrRefClassId = ClassId(clrPackage, Name.identifier("ClrRef"))
	private val stackBufferClassId = ClassId(clrPackage, Name.identifier("StackBuffer"))
	private val spanClassId = ClassId(clrPackage, Name.identifier("Span"))
	private val clrEventClassId = ClassId(clrPackage, Name.identifier("ClrEvent"))
	private val eventSubscriptionClassId = ClassId(clrPackage, Name.identifier("EventSubscription"))

	private val topLevelCallableIds = setOf(
		CallableId(clrPackage, byrefName),
		CallableId(clrPackage, stackBufferName),
		CallableId(clrPackage, clrEventName),
	)
	private val topLevelClassIds = setOf(
		clrRefClassId,
		stackBufferClassId,
		spanClassId,
		clrEventClassId,
	)

	override fun hasPackage(packageFqName: FqName): Boolean = packageFqName == clrPackage

	override fun getTopLevelCallableIds(): Set<CallableId> = topLevelCallableIds

	override fun getTopLevelClassIds(): Set<ClassId> = topLevelClassIds

	override fun generateTopLevelClassLikeDeclaration(classId: ClassId): FirClassLikeSymbol<*>? {
		if (classId !in topLevelClassIds) return null
		return createTopLevelClass(classId, ClrGeneratedKey, ClassKind.CLASS) {
			if (classId == clrEventClassId) modality = Modality.ABSTRACT
			val variance = if (classId == clrEventClassId)
				org.jetbrains.kotlin.types.Variance.OUT_VARIANCE
			else
				org.jetbrains.kotlin.types.Variance.INVARIANT
			typeParameter(Name.identifier("T"), variance, false, ClrGeneratedKey)
		}.symbol
	}

	override fun getCallableNamesForClass(
		classSymbol: FirClassSymbol<*>,
		context: MemberGenerationContext,
	): Set<Name> = when (classSymbol.classId) {
		clrRefClassId -> setOf(Name.identifier("getValue"), Name.identifier("setValue"))
		stackBufferClassId -> setOf(
			Name.identifier("size"),
			Name.identifier("get"),
			Name.identifier("set"),
			Name.identifier("asSpan"),
		)
		clrEventClassId -> setOf(
			Name.identifier("subscribe"),
			Name.identifier("invoke"),
			Name.identifier("getValue"),
			SpecialNames.INIT,
		)
		else -> emptySet()
	}

	override fun generateProperties(
		callableId: CallableId,
		context: MemberGenerationContext?,
	): List<FirPropertySymbol> {
		val owner = context?.owner ?: return emptyList()
		if (owner.classId != stackBufferClassId || callableId.callableName.asString() != "size")
			return emptyList()
		return listOf(
			createMemberProperty(
				owner,
				ClrGeneratedKey,
				callableId.callableName,
				session.builtinTypes.intType.coneType,
				true,
				false,
			).symbol,
		)
	}

	override fun generateConstructors(context: MemberGenerationContext): List<FirConstructorSymbol> {
		if (context.owner.classId != clrEventClassId) return emptyList()
		return listOf(
			createConstructor(context.owner, ClrGeneratedKey, isPrimary = true) {
				visibility = Visibilities.Private
			}.symbol,
		)
	}

	override fun generateFunctions(
		callableId: CallableId,
		context: MemberGenerationContext?,
	): List<FirNamedFunctionSymbol> {
		val owner = context?.owner
		if (owner == null) return generateTopLevelFunction(callableId)

		return when (owner.classId) {
			stackBufferClassId -> generateStackBufferFunction(callableId, owner)
			clrRefClassId -> generateClrRefFunction(callableId, owner)
			clrEventClassId -> generateClrEventFunction(callableId, owner)
			else -> emptyList()
		}
	}

	private fun generateTopLevelFunction(callableId: CallableId): List<FirNamedFunctionSymbol> {
		val function = when (callableId.callableName) {
			byrefName -> createTopLevelFunction(
				ClrGeneratedKey,
				callableId,
				{ typeParameters -> clrRefOf(typeParameters[0].symbol.constructType(emptyArray(), false)) },
			) {
				typeParameter(Name.identifier("T"), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey)
				valueParameter(Name.identifier("x"), { typeParameters ->
					typeParameters[0].symbol.constructType(emptyArray(), false)
				})
			}

			stackBufferName -> createTopLevelFunction(
				ClrGeneratedKey,
				callableId,
				{ typeParameters -> typeParameters[1].symbol.constructType(emptyArray(), false) },
			) {
				typeParameter(Name.identifier("T"), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey)
				typeParameter(Name.identifier("R"), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey)
				valueParameter(Name.identifier("n"), session.builtinTypes.intType.coneType)
				valueParameter(Name.identifier("block"), { typeParameters ->
					coneFunctionType(
						listOf(stackBufferOf(typeParameters[0].symbol.constructType(emptyArray(), false))),
						typeParameters[1].symbol.constructType(emptyArray(), false),
					)
				})
			}

			clrEventName -> createTopLevelFunction(
				ClrGeneratedKey,
				callableId,
				{ clrEventOf(session.builtinTypes.nothingType.coneType) },
			) {}

			else -> return emptyList()
		}
		return listOf(function.symbol)
	}

	private fun generateStackBufferFunction(
		callableId: CallableId,
		owner: FirClassSymbol<*>,
	): List<FirNamedFunctionSymbol> {
		val elementType = owner.typeParameterSymbols.first().constructType(emptyArray(), false)
		val intType = session.builtinTypes.intType.coneType
		val function = when (callableId.callableName.asString()) {
			"get" -> createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, elementType) {
				status { isOperator = true }
				valueParameter(Name.identifier("index"), intType)
			}
			"set" -> createMemberFunction(
				owner,
				ClrGeneratedKey,
				callableId.callableName,
				session.builtinTypes.unitType.coneType,
			) {
				status { isOperator = true }
				valueParameter(Name.identifier("index"), intType)
				valueParameter(Name.identifier("value"), elementType)
			}
			"asSpan" -> createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, spanOf(elementType)) {}
			else -> return emptyList()
		}
		return listOf(function.symbol)
	}

	private fun generateClrRefFunction(
		callableId: CallableId,
		owner: FirClassSymbol<*>,
	): List<FirNamedFunctionSymbol> {
		val referencedType = owner.typeParameterSymbols.first().constructType(emptyArray(), false)
		val nullableAny = session.builtinTypes.nullableAnyType.coneType
		val propertyType = session.symbolProvider.getClassLikeSymbolByClassId(
			ClassId(FqName("kotlin.reflect"), Name.identifier("KProperty")),
		)?.constructType(arrayOf(org.jetbrains.kotlin.fir.types.ConeStarProjection), false) ?: nullableAny

		val function = when (callableId.callableName.asString()) {
			"getValue" -> createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, referencedType) {
				status { isOperator = true }
				valueParameter(Name.identifier("thisRef"), nullableAny)
				valueParameter(Name.identifier("property"), propertyType)
			}
			"setValue" -> createMemberFunction(
				owner,
				ClrGeneratedKey,
				callableId.callableName,
				session.builtinTypes.unitType.coneType,
			) {
				status { isOperator = true }
				valueParameter(Name.identifier("thisRef"), nullableAny)
				valueParameter(Name.identifier("property"), propertyType)
				valueParameter(Name.identifier("value"), referencedType)
			}
			else -> return emptyList()
		}
		return listOf(function.symbol)
	}

	private fun generateClrEventFunction(
		callableId: CallableId,
		owner: FirClassSymbol<*>,
	): List<FirNamedFunctionSymbol> {
		val handlerType = owner.typeParameterSymbols.first().constructType(emptyArray(), false)
		val nullableAny = session.builtinTypes.nullableAnyType.coneType
		val function = when (callableId.callableName.asString()) {
			"subscribe" -> {
				val subscriptionType = session.symbolProvider.getClassLikeSymbolByClassId(eventSubscriptionClassId)
					?.constructType(arrayOf(handlerType), false) ?: nullableAny
				createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, subscriptionType) {
					modality = Modality.ABSTRACT
					valueParameter(Name.identifier("handler"), handlerType)
				}
			}
			"invoke" -> {
				val arrayOfNullableAny = session.symbolProvider.getClassLikeSymbolByClassId(
					ClassId(FqName("kotlin"), Name.identifier("Array")),
				)?.constructType(arrayOf(nullableAny), false) ?: nullableAny
				createMemberFunction(
					owner,
					ClrGeneratedKey,
					callableId.callableName,
					session.builtinTypes.unitType.coneType,
				) {
					modality = Modality.ABSTRACT
					status { isOperator = true }
					valueParameter(Name.identifier("args"), arrayOfNullableAny, isVararg = true)
				}
			}
			"getValue" -> {
				val propertyType = session.symbolProvider.getClassLikeSymbolByClassId(
					ClassId(FqName("kotlin.reflect"), Name.identifier("KProperty")),
				)?.constructType(arrayOf(org.jetbrains.kotlin.fir.types.ConeStarProjection), false) ?: nullableAny
				createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, clrEventOf(handlerType)) {
					modality = Modality.ABSTRACT
					status { isOperator = true }
					valueParameter(Name.identifier("thisRef"), nullableAny)
					valueParameter(Name.identifier("property"), propertyType)
				}
			}
			else -> return emptyList()
		}
		return listOf(function.symbol)
	}

	private fun coneFunctionType(parameters: List<ConeKotlinType>, returnType: ConeKotlinType): ConeKotlinType {
		val classId = ClassId(FqName("kotlin"), Name.identifier("Function${parameters.size}"))
		val symbol = session.symbolProvider.getClassLikeSymbolByClassId(classId)
			?: return session.builtinTypes.nullableAnyType.coneType
		@Suppress("UNCHECKED_CAST")
		val arguments = (parameters + returnType).toTypedArray()
			as Array<org.jetbrains.kotlin.fir.types.ConeTypeProjection>
		return symbol.constructType(arguments, false)
	}

	private fun clrRefOf(argument: ConeKotlinType): ConeKotlinType =
		intrinsicType(clrRefClassId, argument)

	private fun stackBufferOf(argument: ConeKotlinType): ConeKotlinType =
		intrinsicType(stackBufferClassId, argument)

	private fun spanOf(argument: ConeKotlinType): ConeKotlinType =
		intrinsicType(spanClassId, argument)

	private fun clrEventOf(argument: ConeKotlinType): ConeKotlinType =
		intrinsicType(clrEventClassId, argument)

	private fun intrinsicType(classId: ClassId, argument: ConeKotlinType): ConeKotlinType =
		session.symbolProvider.getClassLikeSymbolByClassId(classId)?.constructType(arrayOf(argument), false)
			?: session.builtinTypes.nullableAnyType.coneType
}

/** Registers the compiler-owned Kotlin/CLR intrinsic declarations with FIR. */
class ClrFirExtensionRegistrar : FirExtensionRegistrar() {
	override fun ExtensionRegistrarContext.configurePlugin() {
		+::ClrIntrinsicDeclarationGenerator
	}
}

/** Compiler-plugin entry used by the kotc pipeline. */
class ClrCompilerPluginRegistrar : CompilerPluginRegistrar() {
	override val pluginId: String = "kotc.clr"
	override val supportsK2: Boolean = true

	override fun ExtensionStorage.registerExtensions(configuration: CompilerConfiguration) {
		FirExtensionRegistrarAdapter.registerExtension(ClrFirExtensionRegistrar())
	}
}
