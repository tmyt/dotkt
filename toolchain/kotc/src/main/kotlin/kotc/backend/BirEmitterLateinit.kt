package kotc.backend

import org.jetbrains.kotlin.ir.declarations.IrClass
import org.jetbrains.kotlin.ir.IrElement
import org.jetbrains.kotlin.ir.declarations.IrFile
import org.jetbrains.kotlin.ir.declarations.IrProperty
import org.jetbrains.kotlin.cli.common.messages.CompilerMessageSeverity
import org.jetbrains.kotlin.ir.expressions.IrCall
import org.jetbrains.kotlin.ir.expressions.IrPropertyReference
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import org.jetbrains.kotlin.ir.util.resolveFakeOverride
import org.jetbrains.kotlin.ir.visitors.IrVisitorVoid
import org.jetbrains.kotlin.ir.visitors.acceptChildrenVoid
import org.jetbrains.kotlin.ir.visitors.acceptVoid

/** ClrField opts out of the accessors required by a lateinit property's source contract. */
internal fun BirEmitter.validateLateinitAnnotations(file: IrFile) {
	file.acceptVoid(object : IrVisitorVoid() {
		override fun visitElement(element: IrElement) { element.acceptChildrenVoid(this) }
		override fun visitProperty(declaration: IrProperty) {
			if (!declaration.isFakeOverride && declaration.isLateinit && isClrField(declaration)) {
				hadError = true
				messageCollector?.report(CompilerMessageSeverity.ERROR,
					"@ClrField cannot be applied to a lateinit property; lateinit requires property accessors",
					locationOf(declaration))
			}
			declaration.acceptChildrenVoid(this)
		}
	})
}

/** Preserve the frontend-selected lexical initialization query before materializing an ordinary property reference. */
internal fun BirEmitter.lateinitInitializationQuery(call: IrCall): String? {
	val callee = call.symbol.owner
	val intrinsic = callee.correspondingPropertySymbol?.owner ?: return null
	if (intrinsic.fqNameWhenAvailable?.asString() != "kotlin.isInitialized" || intrinsic.getter !== callee)
		return null
	val reference = extensionReceiver(call) as? IrPropertyReference
		?: error("Frontend-validated lateinit initialization query has no property literal")
	val selectedGetter = reference.getter?.owner ?: reference.symbol.owner.getter
		?: error("Frontend-validated lateinit initialization query has no getter declaration")
	val declaration = if (selectedGetter.isFakeOverride)
		selectedGetter.resolveFakeOverride() ?: selectedGetter else selectedGetter
	val receiver = propRefDispatchReceiver(reference)
	val owner = declaration.parent as? IrClass
	val ownerFact = if (owner == null || receiver == null) "" else
		""","ownerType":${ownerSpec(owner, receiver?.type).toJson()}"""
	val receiverFact = receiver?.let { ""","recv":${expr(it)}""" }.orEmpty()
	return """{"k":"lateinitIsInitialized","lateinitDeclarationId":${str(declarationId(declaration))}$ownerFact$receiverFact}"""
}
