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

		val codegen = CSharpCodegen()
		for (irFile in moduleFragment.files) {
			val csharp = codegen.generateFile(irFile)
			if (csharp.isBlank()) continue
			val baseName = File(irFile.fileEntry.name).name.removeSuffix(".kt")
			File(outputDir, "$baseName.cs").writeText(csharp)
		}

		return ClrBackendArtifact(ExitCode.OK)
	}
}
