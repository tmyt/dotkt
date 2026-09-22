@file:OptIn(org.jetbrains.kotlin.fir.symbols.SymbolInternals::class)

package kotc.pipeline

import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.fir.containingClassLookupTag
import org.jetbrains.kotlin.fir.declarations.FirCallableDeclaration
import org.jetbrains.kotlin.fir.declarations.FirNamedFunction
import org.jetbrains.kotlin.fir.declarations.FirProperty
import org.jetbrains.kotlin.fir.declarations.utils.isStatic
import org.jetbrains.kotlin.fir.resolve.calls.candidate.Candidate
import org.jetbrains.kotlin.fir.resolve.calls.overloads.ConeCallConflictResolver
import org.jetbrains.kotlin.fir.resolve.calls.overloads.ConeCallConflictResolverFactory
import org.jetbrains.kotlin.fir.resolve.isSubclassOf
import org.jetbrains.kotlin.fir.resolve.toRegularClassSymbol
import org.jetbrains.kotlin.fir.scopes.impl.FirStandardOverrideChecker

object ClrStaticConflictResolverFactory : ConeCallConflictResolverFactory() {
	override fun createAdditionalResolvers(session: FirSession): List<ConeCallConflictResolver> = listOf(ClrStaticConflictResolver(session))
}

/** Among applicable, visible class statics, a nearer declaration hides the same inherited member. */
private class ClrStaticConflictResolver(private val session: FirSession) : ConeCallConflictResolver() {
	private val overrideChecker = FirStandardOverrideChecker(session)
	override fun chooseMaximallySpecificCandidates(candidates: Set<Candidate>): Set<Candidate> =
		candidates.filterTo(linkedSetOf()) { base -> candidates.none { nearer -> hides(nearer, base) } }

	private fun hides(nearer: Candidate, base: Candidate): Boolean {
		if (nearer === base) return false
		val member = nearer.symbol.fir as? FirCallableDeclaration ?: return false
		val inherited = base.symbol.fir as? FirCallableDeclaration ?: return false
		if (!member.isStatic || !inherited.isStatic || member.symbol.name != inherited.symbol.name) return false
		val owner = member.containingClassLookupTag()?.toRegularClassSymbol(session) ?: return false
		val baseOwner = inherited.containingClassLookupTag() ?: return false
		if (!owner.isSubclassOf(baseOwner, session, isStrict = true, lookupInterfaces = false)) return false
		return when {
			member is FirProperty && inherited is FirProperty -> true
			member is FirNamedFunction && inherited is FirNamedFunction ->
				overrideChecker.isOverriddenFunction(member, inherited, ignoreVisibility = true)
			else -> false
		}
	}
}
