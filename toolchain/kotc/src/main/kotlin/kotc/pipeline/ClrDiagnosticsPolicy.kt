package kotc.pipeline

import org.jetbrains.kotlin.KtSourceFile
import org.jetbrains.kotlin.cli.common.diagnosticsCollector
import org.jetbrains.kotlin.config.CompilerConfiguration
import org.jetbrains.kotlin.diagnostics.DiagnosticContext
import org.jetbrains.kotlin.diagnostics.KtDiagnostic
import org.jetbrains.kotlin.diagnostics.impl.BaseDiagnosticsCollector
import org.jetbrains.kotlin.fir.analysis.diagnostics.jvm.FirJvmErrors
import org.jetbrains.kotlin.fir.analysis.diagnostics.wasm.FirWasmErrors

/**
 * Removes diagnostics that belong to the JVM representation rather than Kotlin semantics.
 *
 * The common frontend already represents `value class` directly. Requiring `@JvmInline` is a
 * JVM-specific opt-in enforced by a JVM checker; it has no meaning in the CLR backend and must
 * not leak into Kotlin/CLR source.
 */
internal fun CompilerConfiguration.installClrDiagnosticsPolicy() {
	val delegate = diagnosticsCollector
	if (delegate !is ClrDiagnosticsCollector) {
		diagnosticsCollector = ClrDiagnosticsCollector(delegate)
	}
}

private class ClrDiagnosticsCollector(
	private val delegate: BaseDiagnosticsCollector,
) : BaseDiagnosticsCollector() {
	override val diagnostics: List<KtDiagnostic>
		get() = delegate.diagnostics

	override val diagnosticsByFile: Map<KtSourceFile?, List<KtDiagnostic>>
		get() = delegate.diagnosticsByFile

	override val hasErrors: Boolean
		get() = delegate.hasErrors

	override val hasWarningsForWError: Boolean
		get() = delegate.hasWarningsForWError

	override fun report(diagnostic: KtDiagnostic?, context: DiagnosticContext) {
		if (diagnostic?.factory == FirJvmErrors.VALUE_CLASS_WITHOUT_JVM_INLINE_ANNOTATION) return
		// The common metadata frontend currently installs WASI's platform checker as well. CLR external functions are
		// governed by the DllImport contract in kotc/bir2cir, so requiring the unrelated @WasmImport marker here would
		// reject every valid CLR declaration before our target-specific validation can run.
		if (diagnostic?.factory == FirWasmErrors.WASI_EXTERNAL_FUNCTION_WITHOUT_IMPORT) return
		delegate.report(diagnostic, context)
	}
}
