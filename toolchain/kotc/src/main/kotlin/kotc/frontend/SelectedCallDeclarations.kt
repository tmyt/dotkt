@file:OptIn(
	org.jetbrains.kotlin.fir.symbols.SymbolInternals::class,
	org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI::class,
)

package kotc.frontend

import org.jetbrains.kotlin.fir.FirElement
import org.jetbrains.kotlin.fir.backend.Fir2IrComponents
import org.jetbrains.kotlin.fir.declarations.FirFile
import org.jetbrains.kotlin.fir.expressions.FirCallableReferenceAccess
import org.jetbrains.kotlin.fir.expressions.FirQualifiedAccessExpression
import org.jetbrains.kotlin.fir.references.FirResolvedNamedReference
import org.jetbrains.kotlin.fir.symbols.impl.FirNamedFunctionSymbol
import org.jetbrains.kotlin.fir.unwrapFakeOverrides
import org.jetbrains.kotlin.fir.visitors.FirDefaultVisitorVoid
import org.jetbrains.kotlin.ir.declarations.IrSimpleFunction

/**
 * Preserve FIR's selected declaration before Fir2Ir merges coincident inherited signatures.
 *
 * Two overloads can coincide only after substituting their owner parameters. The resulting IR fake override can
 * contain both overridden symbols, even though FIR selected just one of them. Walking that merged override graph
 * cannot recover the selection. Capture its original Kotlin declaration instead, and translate that exact symbol
 * through Fir2Ir's declaration storage. No CLR member selection or signature matching happens here.
 */
object SelectedCallDeclarations {
	private data class Key(val file: String, val end: Int, val name: String, val reference: Boolean)
	private val source = linkedMapOf<Key, FirNamedFunctionSymbol>()
	private val ir = mutableMapOf<Key, IrSimpleFunction>()

	fun reset() { source.clear(); ir.clear() }

	fun capture(files: List<FirFile>) {
		for (file in files) {
			val path = file.sourceFile?.path ?: continue
			file.accept(object : FirDefaultVisitorVoid() {
				override fun visitElement(element: FirElement) {
					element.acceptChildren(this)
					val access = element as? FirQualifiedAccessExpression ?: return
					val symbol = (access.calleeReference as? FirResolvedNamedReference)?.resolvedSymbol
						as? FirNamedFunctionSymbol ?: return
					val original = symbol.unwrapFakeOverrides()
					// An unsubstituted declaration already survives as its own IR symbol.
					if (original === symbol) return
					val location = access.source ?: return
					val key = Key(path, location.endOffset, symbol.name.asString(), access is FirCallableReferenceAccess)
					val previous = source.putIfAbsent(key, original)
					check(previous == null || previous === original) { "Conflicting selected declarations at $key" }
				}
			})
		}
	}

	fun convertDeclarations(components: Fir2IrComponents) {
		for ((key, symbol) in source)
			ir[key] = components.declarationStorage.getIrFunctionSymbol(symbol).owner as IrSimpleFunction
	}

	fun at(file: String?, end: Int, name: String, reference: Boolean = false): IrSimpleFunction? =
		file?.let { ir[Key(it, end, name, reference)] }
}
