package clrc.pipeline

import clrc.backend.ClrBackendPhase
import org.jetbrains.kotlin.backend.common.phaser.then
import org.jetbrains.kotlin.cli.common.arguments.K2JVMCompilerArguments
import org.jetbrains.kotlin.cli.pipeline.AbstractCliPipeline
import org.jetbrains.kotlin.cli.pipeline.jvm.JvmConfigurationPipelinePhase
import org.jetbrains.kotlin.cli.pipeline.jvm.JvmFir2IrPipelinePhase
import org.jetbrains.kotlin.cli.pipeline.jvm.JvmFrontendPipelinePhase
import org.jetbrains.kotlin.util.PerformanceManager

/**
 * Kotlin/CLR compiler driver.
 *
 * We reuse the official JVM phases for everything up to Kotlin IR, and swap only the final
 * backend phase for our own. This is the production-correct seam: the frontend/Fir2Ir keep
 * resolving against the real kotlin-stdlib, and we take ownership exactly where target codegen
 * begins. Later, BCL binding will replace [JvmFrontendPipelinePhase] with a CLR-aware frontend.
 */
class ClrCliPipeline(
	override val defaultPerformanceManager: PerformanceManager,
) : AbstractCliPipeline<K2JVMCompilerArguments>() {
	override fun createCompoundPhase(arguments: K2JVMCompilerArguments) =
		JvmConfigurationPipelinePhase then
			JvmFrontendPipelinePhase then
			JvmFir2IrPipelinePhase then
			ClrBackendPhase
}
