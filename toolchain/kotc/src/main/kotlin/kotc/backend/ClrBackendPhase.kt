package kotc.backend

import org.jetbrains.kotlin.cli.common.ExitCode
import org.jetbrains.kotlin.cli.pipeline.CheckCompilationErrors
import org.jetbrains.kotlin.cli.pipeline.PipelineArtifactWithExitCode
import org.jetbrains.kotlin.cli.pipeline.PipelinePhase
import org.jetbrains.kotlin.cli.pipeline.jvm.JvmFir2IrPipelineArtifact
import org.jetbrains.kotlin.config.JVMConfigurationKeys
import org.jetbrains.kotlin.ir.util.dump
import java.io.File

/** Final artifact of the CLR pipeline. Carries the process exit code. */
class ClrBackendArtifact(override val exitCode: ExitCode) : PipelineArtifactWithExitCode()

/**
 * The one phase we own. Everything before it (Configuration / Frontend / Fir2Ir) is the stock
 * JVM pipeline, so by the time we get here [input] already holds fully-resolved Kotlin IR.
 *
 * It dumps the resolved IR (a debugging foothold) and walks each file to emit BIR — the portable backend IR
 * that `tools/ilemit` turns into CIL. (The retired C#-text backend was removed; BIR -> ilemit is the sole
 * shipping path. See docs/csharp-retirement-design.md.)
 */
object ClrBackendPhase : PipelinePhase<JvmFir2IrPipelineArtifact, ClrBackendArtifact>(
	name = "ClrBackendPhase",
	postActions = setOf(CheckCompilationErrors.CheckDiagnosticCollector),
) {
	override fun executePhase(input: JvmFir2IrPipelineArtifact): ClrBackendArtifact {
		val moduleFragment = input.result.irModuleFragment
		val outputDir = input.configuration.get(JVMConfigurationKeys.OUTPUT_DIRECTORY)
			?: File("build/clr-out").also { it.mkdirs() }
		outputDir.mkdirs()

		File(outputDir, "KIR@Raw.txt").writeText(moduleFragment.dump())

		val messageCollector = input.configuration.get(
			org.jetbrains.kotlin.config.CommonConfigurationKeys.MESSAGE_COLLECTOR_KEY)
		val bir = BirEmitter(messageCollector)
		// The BIR file name is derived from the source file's BASENAME — but the stdlib has several same-named files in
		// different dirs (3x Collections.kt: src/kotlin, src/kotlin/collections, clr/builtins). Disambiguate with a
		// per-basename counter so they don't OVERWRITE each other (clr/builtins/Collections.kt's interface defs were lost).
		val usedNames = HashMap<String, Int>()
		for (irFile in moduleFragment.files) {
			var baseName = File(irFile.fileEntry.name).name.removeSuffix(".kt")
			val seen = usedNames.merge(baseName, 1) { a, b -> a + b }!!
			if (seen > 1) baseName = "${baseName}__$seen"
			val birJson = bir.emitFile(irFile)
			if (birJson.isNotBlank()) File(outputDir, "$baseName.bir.json").writeText(birJson)
		}

		// An unsupported construct was reported (with source location) -> fail the compile here, so the build stops
		// with a clear diagnostic instead of producing BIR that crashes ilemit downstream.
		return ClrBackendArtifact(if (bir.hadError) ExitCode.COMPILATION_ERROR else ExitCode.OK)
	}
}
