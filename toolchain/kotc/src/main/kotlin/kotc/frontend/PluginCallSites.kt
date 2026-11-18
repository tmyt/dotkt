package kotc.frontend

import org.jetbrains.kotlin.backend.common.extensions.IrGenerationExtension
import org.jetbrains.kotlin.backend.common.extensions.IrPluginContext
import org.jetbrains.kotlin.ir.IrElement
import org.jetbrains.kotlin.ir.declarations.IrModuleFragment
import org.jetbrains.kotlin.ir.expressions.IrDeclarationReference
import org.jetbrains.kotlin.ir.symbols.IrSymbol
import org.jetbrains.kotlin.ir.visitors.IrVisitorVoid
import org.jetbrains.kotlin.ir.visitors.acceptChildrenVoid
import org.jetbrains.kotlin.ir.visitors.acceptVoid

/** A source span survives an IR rewrite; FIR's selection belongs to the original target, not the span alone. */
internal object PluginCallSites {
    private var targets: Map<Pair<String, Int>, Set<IrSymbol>> = emptyMap()

    fun guard(extensions: List<IrGenerationExtension>): List<IrGenerationExtension> {
        targets = emptyMap()
        if (extensions.isEmpty()) return extensions
        val sites = (ClrStaticOwners.sites() + SelectedCallDeclarations.sites()).associateWith { mutableSetOf<IrSymbol>() }
        if (sites.isEmpty()) return extensions
        targets = sites
        return listOf(object : IrGenerationExtension {
            override fun generate(moduleFragment: IrModuleFragment, pluginContext: IrPluginContext) {
                for (file in moduleFragment.files) file.acceptVoid(object : IrVisitorVoid() {
                    override fun visitElement(element: IrElement) {
                        if (element is IrDeclarationReference)
                            sites[file.fileEntry.name to element.endOffset]?.add(element.symbol)
                        element.acceptChildrenVoid(this)
                    }
                })
            }
        }) + extensions
    }

    fun sourceOf(file: String?, element: IrElement): String? {
        if (file == null || targets.isEmpty() || element !is IrDeclarationReference) return file
        val original = targets[file to element.endOffset] ?: return file
        return file.takeIf { element.symbol in original }
    }
}
