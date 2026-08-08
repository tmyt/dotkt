package kotc.backend

import org.jetbrains.kotlin.fir.declarations.FirCallableDeclaration
import org.jetbrains.kotlin.fir.declarations.utils.isCompanionExtension
import org.jetbrains.kotlin.fir.lazy.AbstractFir2IrLazyDeclaration
import org.jetbrains.kotlin.ir.declarations.IrDeclaration
import org.jetbrains.kotlin.ir.declarations.IrFunction
import org.jetbrains.kotlin.ir.declarations.IrParameterKind
import org.jetbrains.kotlin.ir.declarations.IrProperty
import org.jetbrains.kotlin.ir.declarations.IrSimpleFunction
import org.jetbrains.kotlin.ir.declarations.IrValueParameter
import org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI

/**
 * A Kotlin 2.4 COMPANION EXTENSION (`companion fun C.foo()`) reached ACROSS A MODULE BOUNDARY.
 *
 * Its receiver is not a parameter: nothing is passed at `C.foo(x)`, and the compiled method has no slot for one.
 * fir2ir honors that when it converts SOURCE (`Fir2IrCallableDeclarationsGenerator` skips the extension-receiver
 * parameter for a companion extension), but its LAZY declaration builder — the one used for a declaration loaded
 * from a library — adds the receiver parameter unconditionally. So the same declaration has two different IR
 * shapes depending on where it came from, and the library shape declares a parameter that does not exist.
 *
 * A lazy declaration still carries the FIR it was built from, which holds the fact directly, so this reads it back
 * rather than inferring anything from arity or naming. A source-compiled declaration is never one of these (its
 * receiver is already gone), and every other declaration answers false.
 */
@OptIn(UnsafeDuringIrConstructionAPI::class)
internal fun isCompanionExtensionCallee(decl: IrDeclaration): Boolean {
	val fir = when (decl) {
		// A property accessor's own FIR carries no receiver — the receiver belongs to the property — so ask the
		// property it accesses.
		is IrSimpleFunction -> decl.correspondingPropertySymbol?.owner?.let { return isCompanionExtensionCallee(it) }
			?: (decl as? AbstractFir2IrLazyDeclaration<*>)?.fir
		is IrProperty -> (decl as? AbstractFir2IrLazyDeclaration<*>)?.fir
		else -> null
	}
	return (fir as? FirCallableDeclaration)?.isCompanionExtension == true
}

/**
 * The declaration's extension-receiver parameter — the one that becomes the leading physical `__self` argument —
 * or null when it has none. A companion extension has none by definition; see [isCompanionExtensionCallee] for why
 * a cross-module one can nevertheless appear to declare one.
 */
internal fun extensionReceiverParam(fn: IrFunction): IrValueParameter? =
	if (isCompanionExtensionCallee(fn)) null
	else fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
