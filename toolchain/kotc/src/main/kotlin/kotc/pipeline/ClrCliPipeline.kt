@file:OptIn(org.jetbrains.kotlin.compiler.plugin.ExperimentalCompilerApi::class)

package kotc.pipeline

import kotc.backend.ClrBackendPhase
import kotc.frontend.ClrCompilerPluginRegistrar
import org.jetbrains.kotlin.backend.common.phaser.then
import org.jetbrains.kotlin.cli.common.arguments.K2JVMCompilerArguments
import org.jetbrains.kotlin.cli.pipeline.AbstractCliPipeline
import org.jetbrains.kotlin.cli.pipeline.ConfigurationPipelineArtifact
import org.jetbrains.kotlin.cli.pipeline.PipelinePhase
import org.jetbrains.kotlin.cli.pipeline.jvm.JvmConfigurationPipelinePhase
import org.jetbrains.kotlin.cli.pipeline.jvm.JvmFir2IrPipelinePhase
import org.jetbrains.kotlin.cli.pipeline.jvm.JvmFrontendPipelinePhase
import org.jetbrains.kotlin.compiler.plugin.CompilerPluginRegistrar
import org.jetbrains.kotlin.util.PerformanceManager

/**
 * Registers our CLR FIR extensions into the (reused) JVM frontend by adding a compiler-plugin
 * registrar to the configuration, after it is built and before the frontend creates the session.
 * This is the S5 seam: no need to replace [JvmFrontendPipelinePhase] — the frontend picks up
 * `COMPILER_PLUGIN_REGISTRARS` when it sets up the project, and our registrar installs the FIR
 * type-injection extension (façade-free `import System.*`).
 */
object ClrPluginRegistrationPhase : PipelinePhase<ConfigurationPipelineArtifact, ConfigurationPipelineArtifact>(
	name = "ClrPluginRegistration",
) {
	override fun executePhase(input: ConfigurationPipelineArtifact): ConfigurationPipelineArtifact {
		input.configuration.add(CompilerPluginRegistrar.COMPILER_PLUGIN_REGISTRARS, ClrCompilerPluginRegistrar())
		return input
	}
}

/**
 * Kotlin/CLR compiler driver.
 *
 * We reuse the official JVM phases for everything up to Kotlin IR, and swap only the final
 * backend phase for our own. This is the production-correct seam: the frontend/Fir2Ir keep
 * resolving against the real kotlin-stdlib, and we take ownership exactly where target codegen
 * begins. [ClrPluginRegistrationPhase] inserts CLR-aware FIR extensions (S5) without replacing
 * the JVM frontend.
 */
class ClrCliPipeline(
	override val defaultPerformanceManager: PerformanceManager,
) : AbstractCliPipeline<K2JVMCompilerArguments>() {
	override fun createCompoundPhase(arguments: K2JVMCompilerArguments) =
		JvmConfigurationPipelinePhase then
			ClrPluginRegistrationPhase then
			JvmFrontendPipelinePhase then
			JvmFir2IrPipelinePhase then
			ClrBackendPhase
}
