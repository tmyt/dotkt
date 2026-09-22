@file:OptIn(org.jetbrains.kotlin.fir.symbols.SymbolInternals::class)

package kotc.pipeline

import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.fir.FirElement
import org.jetbrains.kotlin.fir.SessionConfiguration
import org.jetbrains.kotlin.fir.builder.PsiRawFirBuilder
import org.jetbrains.kotlin.fir.declarations.FirClass
import org.jetbrains.kotlin.fir.declarations.FirResolvePhase
import org.jetbrains.kotlin.fir.declarations.FirTypeAlias
import org.jetbrains.kotlin.fir.declarations.FirFile
import org.jetbrains.kotlin.fir.declarations.FirDeclarationOrigin
import org.jetbrains.kotlin.fir.declarations.FirTypeParameter
import org.jetbrains.kotlin.fir.declarations.utils.isStatic
import org.jetbrains.kotlin.fir.declarations.builder.FirRegularClassBuilder
import org.jetbrains.kotlin.fir.deserialization.FirDeserializationExtension
import org.jetbrains.kotlin.fir.resolve.ScopeSession
import org.jetbrains.kotlin.fir.resolve.lookupSuperTypes
import org.jetbrains.kotlin.fir.resolve.toRegularClassSymbol
import org.jetbrains.kotlin.fir.resolve.providers.firProvider
import org.jetbrains.kotlin.fir.resolve.providers.impl.FirProviderImpl
import org.jetbrains.kotlin.fir.scopes.*
import org.jetbrains.kotlin.fir.expressions.FirQualifiedAccessExpression
import org.jetbrains.kotlin.fir.expressions.FirResolvedQualifier
import org.jetbrains.kotlin.fir.expressions.FirVariableAssignment
import org.jetbrains.kotlin.fir.expressions.FirPropertyAccessExpression
import org.jetbrains.kotlin.fir.expressions.unwrapLValue
import org.jetbrains.kotlin.fir.expressions.builder.buildResolvedQualifier
import org.jetbrains.kotlin.fir.references.FirResolvedNamedReference
import org.jetbrains.kotlin.fir.resolve.providers.symbolProvider
import org.jetbrains.kotlin.fir.symbols.impl.FirCallableSymbol
import org.jetbrains.kotlin.fir.symbols.impl.FirNamedFunctionSymbol
import org.jetbrains.kotlin.fir.symbols.impl.FirPropertySymbol
import org.jetbrains.kotlin.fir.symbols.impl.FirVariableSymbol
import org.jetbrains.kotlin.fir.symbols.impl.FirRegularClassSymbol
import org.jetbrains.kotlin.fir.visitors.FirDefaultVisitorVoid
import org.jetbrains.kotlin.fir.types.resolvedType
import org.jetbrains.kotlin.fir.scopes.impl.FirFakeOverrideGenerator
import org.jetbrains.kotlin.fir.resolve.substitution.ConeSubstitutor
import org.jetbrains.kotlin.fir.types.ConeClassLikeType
import org.jetbrains.kotlin.fir.types.coneType
import org.jetbrains.kotlin.name.ClassId
import org.jetbrains.kotlin.name.Name
import org.jetbrains.kotlin.psi.KtFile

/** CLR class qualifiers expose inherited static declarations without creating derived storage or methods. */
private class ClrStaticScopeProvider(private val delegate: FirScopeProvider) : FirScopeProvider() {
	override fun getUseSiteMemberScope(klass: FirClass, useSiteSession: FirSession, scopeSession: ScopeSession,
		memberRequiredPhase: FirResolvePhase?) = delegate.getUseSiteMemberScope(klass, useSiteSession, scopeSession, memberRequiredPhase)
	override fun getTypealiasConstructorScope(typeAlias: FirTypeAlias, useSiteSession: FirSession, scopeSession: ScopeSession) =
		delegate.getTypealiasConstructorScope(typeAlias, useSiteSession, scopeSession)
	override fun getNestedClassifierScope(klass: FirClass, useSiteSession: FirSession, scopeSession: ScopeSession) =
		delegate.getNestedClassifierScope(klass, useSiteSession, scopeSession)
	override fun getStaticCallableMemberScope(klass: FirClass, useSiteSession: FirSession, scopeSession: ScopeSession): FirContainingNamesAwareScope? {
		val scopes = mutableListOf<FirContainingNamesAwareScope>()
		delegate.getStaticCallableMemberScope(klass, useSiteSession, scopeSession)?.let(scopes::add)
		for (baseType in lookupSuperTypes(klass, lookupInterfaces = false, deep = true,
			useSiteSession = useSiteSession, substituteTypes = true)) {
			val base = baseType.lookupTag.toRegularClassSymbol(useSiteSession) ?: continue
			val provider = base.fir.scopeProvider.let { if (it is ClrStaticScopeProvider) it.delegate else it }
			val declared = provider.getStaticCallableMemberScope(base.fir, useSiteSession, scopeSession) ?: continue
			if (baseType.typeArguments.isEmpty()) { scopes += declared; continue }
			val substitutor = baseType.substitutorForSuperType(useSiteSession, base)
			scopes += StaticSubstitutionScope(declared, useSiteSession, baseType, substitutor)
		}
		return scopes.takeIf { it.isNotEmpty() }?.let(::NearestStaticScope)
	}
	override fun getStaticCallableMemberScopeForBackend(klass: FirClass, useSiteSession: FirSession, scopeSession: ScopeSession) =
		delegate.getStaticCallableMemberScopeForBackend(klass, useSiteSession, scopeSession)
}

private class StaticSubstitutionScope(private val scope: FirContainingNamesAwareScope,
	private val session: FirSession, private val owner: ConeClassLikeType, private val substitutor: ConeSubstitutor
) : FirContainingNamesAwareScope() {
	private val functions = mutableMapOf<FirNamedFunctionSymbol, FirNamedFunctionSymbol>()
	private val properties = mutableMapOf<FirPropertySymbol, FirPropertySymbol>()
	private val origin = FirDeclarationOrigin.SubstitutionOverride.CallSite
	override fun getCallableNames() = scope.getCallableNames()
	override fun getClassifierNames() = emptySet<Name>()
	override fun processFunctionsByName(name: Name, processor: (FirNamedFunctionSymbol) -> Unit) {
		scope.processFunctionsByName(name) { original -> processor(functions.getOrPut(original) {
			val member = original.fir
			val symbol = FirFakeOverrideGenerator.createSymbolForSubstitutionOverride(original)
			val (parameters, substitution) = FirFakeOverrideGenerator.createNewTypeParametersAndSubstitutor(
				session, member, symbol, substitutor, origin)
			FirFakeOverrideGenerator.createSubstitutionOverrideFunction(session, symbol, member, owner.lookupTag,
				newDispatchReceiverType = null, origin = origin,
				newReceiverType = member.receiverParameter?.typeRef?.coneType?.let(substitution::substituteOrSelf),
				newContextParameterTypes = member.contextParameters.map { substitution.substituteOrSelf(it.returnTypeRef.coneType) },
				newReturnType = substitution.substituteOrSelf(member.returnTypeRef.coneType),
				newParameterTypes = member.valueParameters.map { substitution.substituteOrSelf(it.returnTypeRef.coneType) },
				newTypeParameters = parameters.map { it as FirTypeParameter })
		}) }
	}
	override fun processPropertiesByName(name: Name, processor: (FirVariableSymbol<*>) -> Unit) {
		scope.processPropertiesByName(name) { original ->
			if (original !is FirPropertySymbol) { processor(original); return@processPropertiesByName }
			processor(properties.getOrPut(original) {
				val member = original.fir
				val symbol = FirFakeOverrideGenerator.createSymbolForSubstitutionOverride(original)
				val (parameters, substitution) = FirFakeOverrideGenerator.createNewTypeParametersAndSubstitutor(
					session, member, symbol, substitutor, origin)
				FirFakeOverrideGenerator.createSubstitutionOverrideProperty(session, symbol, member, owner.lookupTag,
					newDispatchReceiverType = null, origin = origin,
					newReceiverType = member.receiverParameter?.typeRef?.coneType?.let(substitution::substituteOrSelf),
					newContextParameterTypes = member.contextParameters.map { substitution.substituteOrSelf(it.returnTypeRef.coneType) },
					newReturnType = substitution.substituteOrSelf(member.returnTypeRef.coneType),
					newTypeParameters = parameters.map { it as FirTypeParameter })
			})
		}
	}
	@DelicateScopeAPI
	override fun withReplacedSessionOrNull(newSession: FirSession, newScopeSession: ScopeSession) =
		scope.withReplacedSessionOrNull(newSession, newScopeSession)?.let { StaticSubstitutionScope(it, newSession, owner, substitutor) }
}

private class NearestStaticScope(private val scopes: List<FirContainingNamesAwareScope>) : FirContainingNamesAwareScope() {
	override fun getCallableNames() = scopes.flatMapTo(linkedSetOf()) { it.getCallableNames() }
	override fun getClassifierNames() = emptySet<Name>()
	@DelicateScopeAPI
	override fun withReplacedSessionOrNull(newSession: FirSession, newScopeSession: ScopeSession) =
		scopes.withReplacedSessionOrNull(newSession, newScopeSession)?.let(::NearestStaticScope)
	override fun processFunctionsByName(name: Name, processor: (FirNamedFunctionSymbol) -> Unit) {
		for (scope in scopes) {
			val found = mutableListOf<FirNamedFunctionSymbol>()
			scope.processFunctionsByName(name, found::add)
			if (found.isNotEmpty()) { found.forEach(processor); return }
		}
	}
	override fun processPropertiesByName(name: Name, processor: (FirVariableSymbol<*>) -> Unit) {
		for (scope in scopes) {
			val found = mutableListOf<FirVariableSymbol<*>>()
			scope.processPropertiesByName(name, found::add)
			if (found.isNotEmpty()) { found.forEach(processor); return }
		}
	}
}

@OptIn(SessionConfiguration::class)
fun installClrStaticDeserialization(session: FirSession) {
	session.register(FirDeserializationExtension::class, object : FirDeserializationExtension(session) {
		override fun FirRegularClassBuilder.configureDeserializedClass(classId: ClassId) {
			scopeProvider = ClrStaticScopeProvider(scopeProvider)
		}
	})
}

fun FirSession.buildClrFirFromKtFiles(files: Collection<KtFile>) = (firProvider as FirProviderImpl).let { provider ->
	val builder = PsiRawFirBuilder(this, ClrStaticScopeProvider(provider.kotlinScopeProvider))
	files.map { builder.buildFirFile(it).also(provider::recordFile) }
}

/** A static use references its resolved declaration, not an instance override on the written qualifier. */
fun normalizeClrStaticReceivers(session: FirSession, files: List<FirFile>) {
	for (file in files) file.accept(object : FirDefaultVisitorVoid() {
		override fun visitElement(element: FirElement) {
			element.acceptChildren(this)
			if (element is FirQualifiedAccessExpression) normalize(element)
			if (element is FirVariableAssignment) {
				val property = element.unwrapLValue() as? FirPropertyAccessExpression
				val symbol = (property?.calleeReference as? FirResolvedNamedReference)?.resolvedSymbol as? FirPropertySymbol
				if (symbol != null && property.source != null && element.source != null && file.sourceFile != null)
					kotc.frontend.ClrStaticOwners.recordAssignment(file.sourceFile!!.path!!,
						property.source!!.endOffset, element.source!!.endOffset, symbol.name.asString())
			}
		}
		private fun normalize(qualifiedAccessExpression: FirQualifiedAccessExpression) {
			val callable = (qualifiedAccessExpression.calleeReference as? FirResolvedNamedReference)
				?.resolvedSymbol as? FirCallableSymbol<*> ?: return
			if (!callable.isStatic) return
			val receiver = qualifiedAccessExpression.dispatchReceiver as? FirResolvedQualifier ?: return
			val ownerId = callable.callableId?.classId ?: return
			if (receiver.classId == ownerId) return
			val receiverClass = receiver.symbol as? FirRegularClassSymbol ?: return
			val ownerType = lookupSuperTypes(receiverClass.fir, lookupInterfaces = false, deep = true,
				useSiteSession = session, substituteTypes = true).single { it.lookupTag.classId == ownerId }
			val path = file.sourceFile?.path
			val callSource = qualifiedAccessExpression.source
			if (path != null && callSource != null) {
				val kinds = if (callable is FirPropertySymbol) listOf("get", "set") else listOf("call")
				for (kind in kinds) kotc.frontend.ClrStaticOwners.record(path, callSource.endOffset,
					callable.name.asString(), kind, ownerType)
			}
			val owner = session.symbolProvider.getClassLikeSymbolByClassId(ownerId) ?: error("Missing static declaration owner $ownerId")
			val resolvedOwner = buildResolvedQualifier {
				source = receiver.source
				coneTypeOrNull = receiver.resolvedType
				packageFqName = ownerId.packageFqName
				relativeClassFqName = ownerId.relativeClassName
				symbol = owner
				resolvedToCompanionObject = false
			}
			qualifiedAccessExpression.replaceDispatchReceiver(resolvedOwner)
			if (qualifiedAccessExpression.explicitReceiver === receiver)
				qualifiedAccessExpression.replaceExplicitReceiver(resolvedOwner)
		}
	})
}
