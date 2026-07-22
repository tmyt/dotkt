package kotc.backend

import org.jetbrains.kotlin.cli.common.ExitCode
import org.jetbrains.kotlin.cli.common.CLIConfigurationKeys
import org.jetbrains.kotlin.cli.pipeline.CheckCompilationErrors
import org.jetbrains.kotlin.cli.pipeline.PipelineArtifact
import org.jetbrains.kotlin.cli.pipeline.PipelineArtifactWithExitCode
import org.jetbrains.kotlin.cli.pipeline.PipelinePhase
import org.jetbrains.kotlin.config.CompilerConfiguration
import org.jetbrains.kotlin.config.JVMConfigurationKeys
import org.jetbrains.kotlin.fir.pipeline.Fir2IrActualizedResult
import org.jetbrains.kotlin.ir.util.dump
import java.io.File

/** Final artifact of the CLR pipeline. Carries the process exit code. */
class ClrBackendArtifact(
	override val exitCode: ExitCode,
	override val configuration: CompilerConfiguration,
) : PipelineArtifactWithExitCode() {
	@OptIn(PipelineArtifact.CliPipelineInternals::class)
	override fun withCompilerConfiguration(newConfiguration: CompilerConfiguration): ClrBackendArtifact =
		ClrBackendArtifact(exitCode, newConfiguration)
}

/**
 * The one phase we own. Everything before it (Configuration / Frontend / Fir2Ir) is the stock
 * JVM pipeline, so by the time we get here [input] already holds fully-resolved Kotlin IR.
 *
 * It dumps the resolved IR (a debugging foothold) and walks each file to emit BIR — the portable backend IR
 * consumed by bir2cir before ilemit emits CIL. See docs/architecture.md.
 */
object ClrBackendPhase : PipelinePhase<kotc.pipeline.ClrFir2IrPipelineArtifact, ClrBackendArtifact>(
	name = "ClrBackendPhase",
	postActions = setOf(CheckCompilationErrors.CheckDiagnosticCollector),
) {
	override fun executePhase(input: kotc.pipeline.ClrFir2IrPipelineArtifact): ClrBackendArtifact {
		return emit(input.result, input.configuration)
	}
}

private fun emit(result: Fir2IrActualizedResult, configuration: CompilerConfiguration): ClrBackendArtifact {
		val moduleFragment = result.irModuleFragment
		val outputDir = configuration?.get(JVMConfigurationKeys.OUTPUT_DIRECTORY)
			?: configuration?.get(CLIConfigurationKeys.METADATA_DESTINATION_DIRECTORY)
			?: File("build/clr-out").also { it.mkdirs() }
		outputDir.mkdirs()

		File(outputDir, "KIR@Raw.txt").writeText(moduleFragment.dump())

		val messageCollector = configuration?.get(
			org.jetbrains.kotlin.config.CommonConfigurationKeys.MESSAGE_COLLECTOR_KEY)
		val bir = BirEmitter(messageCollector, result.irBuiltIns)
		// The BIR file name is derived from the source file's BASENAME — but the stdlib has several same-named files in
		// different dirs (3x Collections.kt: src/kotlin, src/kotlin/collections, clr/builtins). Disambiguate with a
		// per-basename counter so they don't OVERWRITE each other (clr/builtins/Collections.kt's interface defs were lost).
		val usedNames = HashMap<String, Int>()
		// Per-FILE resilience: an unexpected exception while emitting ONE file must NOT abort the whole loop. The loop
		// walks `moduleFragment.files` in order, so a raw throw (e.g. a mis-shaped-call NPE) silently dropped EVERY file
		// after the offender — the stdlib build lost ~120 type-defs (Sequence/PrimitiveIterators/Continuation/… all live
		// after Maps.kt) yet still reported "success" because the earlier files had already written BIR. Catch, report the
		// crash as a compile ERROR (so the build fails loudly + names the file), and continue so the remaining files still
		// emit and the failure is a single clear diagnostic instead of a catastrophic cascade.
		var crashed = false
		for (irFile in moduleFragment.files) {
			var baseName = File(irFile.fileEntry.name).name.removeSuffix(".kt")
			val seen = usedNames.merge(baseName, 1) { a, b -> a + b }!!
			if (seen > 1) baseName = "${baseName}__$seen"
			val birJson = try {
				bir.emitFile(irFile)
			} catch (e: Throwable) {
				crashed = true
				messageCollector?.report(
					org.jetbrains.kotlin.cli.common.messages.CompilerMessageSeverity.ERROR,
					"BIR emit crashed for ${irFile.fileEntry.name}: ${e.javaClass.simpleName}: ${e.message}")
				""
			}
			if (birJson.isNotBlank()) File(outputDir, "$baseName.bir.json").writeText(birJson)
		}

		// An unsupported construct was reported (with source location), or a file crashed -> fail the compile here, so the
		// build stops with a clear diagnostic instead of producing BIR that crashes ilemit downstream.
		return ClrBackendArtifact(if (bir.hadError || crashed) ExitCode.COMPILATION_ERROR else ExitCode.OK, configuration)
}
