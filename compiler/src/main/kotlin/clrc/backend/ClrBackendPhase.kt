package clrc.backend

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
 * Phase A: just dump the IR so we have a debugging foothold. Lowering + C# codegen come next.
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

		// E-5: the IL backend (BIR -> ilemit) is the shipping path; C# codegen is demoted to a dev/oracle
		// opt-in (`KOTLIN_CLR_EMIT_CS=1`). By default we emit ONLY BIR and never run CSharpCodegen, so the
		// shipping path has no C# dependency and IL-only features can't trip the (frozen) C# backend.
		// See docs/csharp-retirement-design.md / [[il-primary-backend-pivot]].
		val emitCs = System.getenv("KOTLIN_CLR_EMIT_CS") == "1"
		val codegen = if (emitCs) CSharpCodegen() else null
		val messageCollector = input.configuration.get(
			org.jetbrains.kotlin.config.CommonConfigurationKeys.MESSAGE_COLLECTOR_KEY)
		val bir = BirEmitter(messageCollector)
		for (irFile in moduleFragment.files) {
			val baseName = File(irFile.fileEntry.name).name.removeSuffix(".kt")
			if (codegen != null) {
				val csharp = codegen.generateFile(irFile)
				if (csharp.isNotBlank()) File(outputDir, "$baseName.cs").writeText(csharp)
			}
			val birJson = bir.emitFile(irFile)
			if (birJson.isNotBlank()) File(outputDir, "$baseName.bir.json").writeText(birJson)
		}

		// An unsupported construct was reported (with source location) -> fail the compile here in the shipping IL
		// path, so the build stops with a clear diagnostic instead of producing BIR that crashes ilemit downstream.
		// In C#-oracle mode (KOTLIN_CLR_EMIT_CS=1) the C# backend is authoritative, so an IL-only gap doesn't fail it.
		return ClrBackendArtifact(if (bir.hadError && !emitCs) ExitCode.COMPILATION_ERROR else ExitCode.OK)
	}
}
