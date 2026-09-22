@file:OptIn(org.jetbrains.kotlin.fir.symbols.SymbolInternals::class)

package kotc.pipeline

import org.jetbrains.kotlin.descriptors.*
import org.jetbrains.kotlin.fir.*
import org.jetbrains.kotlin.fir.backend.Fir2IrVisibilityConverter
import org.jetbrains.kotlin.fir.declarations.*
import org.jetbrains.kotlin.fir.declarations.impl.FirDeclarationStatusImpl
import org.jetbrains.kotlin.fir.declarations.impl.FirDeclarationStatusWithAlteredDefaults
import org.jetbrains.kotlin.fir.declarations.utils.isStatic
import org.jetbrains.kotlin.fir.expressions.FirExpression
import org.jetbrains.kotlin.fir.extensions.FirExtensionRegistrar
import org.jetbrains.kotlin.fir.extensions.FirStatusTransformerExtension
import org.jetbrains.kotlin.fir.resolve.SupertypeSupplier
import org.jetbrains.kotlin.fir.symbols.FirBasedSymbol
import org.jetbrains.kotlin.fir.symbols.impl.FirClassLikeSymbol
import org.jetbrains.kotlin.fir.types.ConeClassLikeLookupTag
import org.jetbrains.kotlin.name.FqName

/** Protected class statics have no instance-receiver restriction; family access still applies. */
private object ClrProtectedStaticVisibility : Visibility("protected static", true) {
	override fun normalize() = Visibilities.Protected
	override fun mustCheckInImports() = false
	override fun compareTo(visibility: Visibility) = Visibilities.Protected.compareTo(visibility.normalize())
}

private fun FirDeclarationStatus.forClrStatic(isStatic: Boolean): FirDeclarationStatus {
	if (!isStatic || visibility != Visibilities.Protected) return this
	val original = this as FirDeclarationStatusImpl
	val copy = if (this is FirResolvedDeclarationStatus) {
		val resolved = this
		// Deserialization invokes this before the owner class is bound. Keep its effective visibility lazy.
		object : FirDeclarationStatusImpl(ClrProtectedStaticVisibility, resolved.modality), FirResolvedDeclarationStatus {
			override val modality get() = resolved.modality
			override val effectiveVisibility get() = resolved.effectiveVisibility
			override val defaultVisibility get() = resolved.defaultVisibility
			override val defaultModality get() = resolved.defaultModality
		}
	} else FirDeclarationStatusWithAlteredDefaults(ClrProtectedStaticVisibility, modality,
		defaultVisibility, defaultModality)
	return copy.apply {
		for (modifier in FirDeclarationStatusImpl.Modifier.entries) this[modifier] = original[modifier]
	}
}

fun prepareClrStaticVisibility(declaration: FirDeclaration, owner: ConeClassLikeLookupTag) {
	if (declaration !is FirCallableDeclaration || !declaration.isStatic) return
	declaration.containingClassForStaticMemberAttr = owner
	declaration.replaceStatus(declaration.status.forClrStatic(declaration.isStatic))
	if (declaration is FirProperty) {
		declaration.getter?.let { it.replaceStatus(it.status.forClrStatic(declaration.isStatic)) }
		declaration.setter?.let { it.replaceStatus(it.status.forClrStatic(declaration.isStatic)) }
	}
}

object ClrStaticStatusRegistrar : FirExtensionRegistrar() {
	override fun ExtensionRegistrarContext.configurePlugin() {
		+FirStatusTransformerExtension.Factory(::ClrStaticStatusTransformer)
	}
}

private class ClrStaticStatusTransformer(session: FirSession) : FirStatusTransformerExtension(session) {
	override fun needTransformStatus(declaration: FirDeclaration) = declaration is FirCallableDeclaration
	override fun transformStatus(status: FirDeclarationStatus, declaration: FirDeclaration) =
		status.forClrStatic(status.isStatic)
	override fun transformStatus(status: FirDeclarationStatus, propertyAccessor: FirPropertyAccessor,
		containingClass: FirClassLikeSymbol<*>?, containingProperty: FirProperty?, isLocal: Boolean) =
		status.forClrStatic(containingProperty?.isStatic == true)
}

object ClrStaticVisibilityChecker : FirVisibilityChecker() {
	override fun platformVisibilityCheck(declarationVisibility: Visibility, symbol: FirBasedSymbol<*>,
		useSiteFile: FirFile, containingDeclarations: List<FirDeclaration>, dispatchReceiver: FirExpression?,
		session: FirSession, isCallToPropertySetter: Boolean, supertypeSupplier: SupertypeSupplier): Boolean {
		if (declarationVisibility != ClrProtectedStaticVisibility) return true
		val owner = symbol.getOwnerLookupTag() ?: return false
		return canSeeProtectedMemberOf(symbol, containingDeclarations, null, owner, session,
			symbol.isVariableOrNamedFunction(), false, supertypeSupplier)
	}
	override fun platformOverrideVisibilityCheck(packageNameOfDerivedClass: FqName,
		symbolInBaseClass: FirBasedSymbol<*>, visibilityInBaseClass: Visibility) = true
}

object ClrFir2IrVisibilityConverter : Fir2IrVisibilityConverter() {
	override fun convertPlatformVisibility(visibility: Visibility): DescriptorVisibility =
		if (visibility == ClrProtectedStaticVisibility) DescriptorVisibilities.PROTECTED
		else Default.convertToDescriptorVisibility(visibility)
}
