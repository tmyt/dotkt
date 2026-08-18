@file:OptIn(
	org.jetbrains.kotlin.fir.declarations.DirectDeclarationsAccess::class,
	org.jetbrains.kotlin.fir.symbols.SymbolInternals::class,
)

package kotc.frontend

import org.jetbrains.kotlin.fir.FirElement
import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.fir.declarations.FirFile
import org.jetbrains.kotlin.fir.declarations.FirProperty
import org.jetbrains.kotlin.fir.declarations.getAnnotationWithResolvedArgumentsByClassId
import org.jetbrains.kotlin.fir.declarations.getStringArgument
import org.jetbrains.kotlin.fir.declarations.utils.isCompanionExtension
import org.jetbrains.kotlin.fir.expressions.FirDesugaredAssignmentValueReferenceExpression
import org.jetbrains.kotlin.fir.expressions.FirPropertyAccessExpression
import org.jetbrains.kotlin.fir.expressions.FirVariableAssignment
import org.jetbrains.kotlin.fir.expressions.unwrapLValue
import org.jetbrains.kotlin.fir.references.FirResolvedNamedReference
import org.jetbrains.kotlin.fir.visitors.FirDefaultVisitorVoid
import org.jetbrains.kotlin.name.ClassId
import org.jetbrains.kotlin.name.FqName
import org.jetbrains.kotlin.name.Name

/**
 * Frontend-resolved identity for a DLL -> KLIB member-extension property access.
 *
 * Kotlin 2.4 can lower a direct access to a method-generic member-extension property into an [IrCall] whose symbol
 * is a synthetic raw-accessor view. Its remaining property association does not carry the projected property's
 * annotations, even though FIR resolved the source expression to the projected [FirProperty]. Capture the property
 * fact before fir2ir erases it; the backend uses it only to emit the already-resolved Kotlin property name, accessor
 * role and trusted declaration identity. CLR representation remains bir2cir's responsibility.
 */
object ClrProjectedMemberExtensionProperties {
	data class AccessFact(
		val sourceName: String,
		val accessorKind: String,
		val declarationId: String,
	)

	private val byUseFile = java.util.concurrent.ConcurrentHashMap<
		String,
		java.util.concurrent.ConcurrentHashMap<String, AccessFact>,
	>()
	private val poisoned = java.util.concurrent.ConcurrentHashMap.newKeySet<String>()

	private val identityClassId = ClassId.topLevel(FqName("kotlin.clr.KotlinDeclarationIdentity"))
	private val idName = Name.identifier("id")
	private val setterIdName = Name.identifier("setterId")

	private fun accessKey(end: Int, accessorKind: String) = "$end|$accessorKind"
	private fun poisonKey(file: String, end: Int, accessorKind: String) = "$file|$end|$accessorKind"

	fun reset() {
		byUseFile.clear()
		poisoned.clear()
	}

	fun accessAtUse(file: String?, start: Int, end: Int, accessorKind: String): AccessFact? {
		if (file == null || start < 0 || end < start) return null
		// fir2ir narrows a raw accessor IrCall's start to the accessor portion of the source expression, while retaining
		// the expression's end offset (e.g. FIR `receiver.property`, IR `property`). End is therefore the stable source
		// coordinate across the boundary. Conflicting facts for one accessor role at a file/end slot poison the key below
		// and fail closed; getter and setter calls may legitimately share that end in a property-to-property assignment.
		return byUseFile[file]?.get(accessKey(end, accessorKind))
	}

	fun capture(session: FirSession, files: List<FirFile>) {
		for (file in files) {
			val path = file.sourceFile?.path ?: continue
			file.accept(object : FirDefaultVisitorVoid() {
				private val setterLValues = java.util.IdentityHashMap<FirPropertyAccessExpression, Boolean>()

				override fun visitElement(element: FirElement) {
					element.acceptChildren(this)
				}

				override fun visitPropertyAccessExpression(propertyAccessExpression: FirPropertyAccessExpression) {
					if (!setterLValues.containsKey(propertyAccessExpression))
						record(path, propertyAccessExpression, target(propertyAccessExpression), "get", session)
					propertyAccessExpression.acceptChildren(this)
				}

				override fun visitVariableAssignment(variableAssignment: FirVariableAssignment) {
					val lValue = variableAssignment.unwrapLValue() as? FirPropertyAccessExpression
					if (lValue != null) {
						val target = target(lValue)
						record(path, variableAssignment, target, "set", session)
						record(path, lValue, target, "set", session)
						// `x += y` and `x++` resolve to a variable assignment whose wrapped lvalue is also the
						// property read feeding the desugared operator call. The child suppression below prevents
						// that shared node from being misrecorded as an ordinary getter, so preserve its read role
						// explicitly before walking the assignment.
						if (variableAssignment.lValue is FirDesugaredAssignmentValueReferenceExpression)
							record(path, lValue, target, "get", session)
						setterLValues[lValue] = true
					}
					variableAssignment.acceptChildren(this)
					if (lValue != null) setterLValues.remove(lValue)
				}
			})
		}
	}

	private fun target(expression: FirPropertyAccessExpression): FirProperty? = runCatching {
		(expression.calleeReference as? FirResolvedNamedReference)?.resolvedSymbol?.fir as? FirProperty
	}.getOrNull()

	private fun record(
		file: String,
		use: FirElement,
		target: FirProperty?,
		accessorKind: String,
		session: FirSession,
	) {
		if (target == null || target.receiverParameter == null || target.typeParameters.isEmpty() ||
			target.isCompanionExtension) return
		val callableId = target.symbol.callableId ?: return
		if (callableId.classId == null) return
		val annotation = target.symbol.getAnnotationWithResolvedArgumentsByClassId(identityClassId, session) ?: return
		val declarationId = annotation.getStringArgument(if (accessorKind == "set") setterIdName else idName)
			?.takeIf { it.isNotEmpty() } ?: return
		val source = use.source ?: return
		if (source.startOffset < 0 || source.endOffset < source.startOffset) return
		val access = AccessFact(callableId.callableName.asString(), accessorKind, declarationId)
		put(file, source.startOffset, source.endOffset, access)
	}

	private fun put(file: String, start: Int, end: Int, access: AccessFact) {
		if (start < 0 || end < start) return
		val poison = poisonKey(file, end, access.accessorKind)
		if (poison in poisoned) return
		val entries = byUseFile.computeIfAbsent(file) { java.util.concurrent.ConcurrentHashMap() }
		val key = accessKey(end, access.accessorKind)
		val prior = entries.putIfAbsent(key, access)
		if (prior != null && prior != access) {
			entries.remove(key)
			poisoned.add(poison)
		}
	}
}
