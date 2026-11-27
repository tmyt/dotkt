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
import java.io.File
import java.util.TreeMap

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
 * It walks each file to emit BIR — the portable backend IR
 * consumed by bir2cir before ilemit emits CIL. See docs/architecture.md.
 */
object ClrBackendPhase : PipelinePhase<kotc.pipeline.ClrFir2IrPipelineArtifact, ClrBackendArtifact>(
	name = "ClrBackendPhase",
	postActions = setOf(CheckCompilationErrors.CheckDiagnosticCollector),
) {
	override fun executePhase(input: kotc.pipeline.ClrFir2IrPipelineArtifact): ClrBackendArtifact {
		return emit(input.result, input.configuration, input.headers, input.pluginSources)
	}
}

@OptIn(org.jetbrains.kotlin.config.MessageCollectorAccess::class)
private fun emit(result: Fir2IrActualizedResult, configuration: CompilerConfiguration,
    headers: TypeAliasDeclarations.Headers, pluginSources: PluginCallableSources?): ClrBackendArtifact {
		val moduleFragment = result.irModuleFragment
		val outputDir = configuration?.get(JVMConfigurationKeys.OUTPUT_DIRECTORY)
			?: configuration?.get(CLIConfigurationKeys.METADATA_DESTINATION_DIRECTORY)
			?: File("build/clr-out").also { it.mkdirs() }
		outputDir.mkdirs()
		val format = configuration.get(kotc.bir.BirFileCodec.outputFormat) ?: kotc.bir.BirFileFormat.JSON

		val messageCollector = configuration?.get(
			org.jetbrains.kotlin.config.CommonConfigurationKeys.MESSAGE_COLLECTOR_KEY)
		val bir = BirEmitter(messageCollector, result.irBuiltIns, moduleFragment.files.toSet())
		bir.constructedOverrides = kotc.frontend.FirOverrideConstructions(result.components)
		val sourceCallables = pluginSources?.select(result)
		// Which local `var`s need a heap ref-cell is a property of the variable, not of the emission frame, and one
		// declaration's expression can be emitted in another file's frame (an omitted default argument is inlined at
		// the caller). Decide it ONCE over the whole module, before any file is emitted, so every emission root agrees.
		bir.initRefCells(moduleFragment)
		// MSBuild treats artifact paths case-insensitively even on Linux. Reserve each emitted stem,
		// including generated suffixes: another source can itself be named Foo__2.kt.
		val usedNames = TreeMap<String, Int>(String.CASE_INSENSITIVE_ORDER)
		fun artifactStem(source: String): String {
			// Leave room for the suffix and codec extension in a 255-byte path component.
			// Non-ASCII case pairs can differ between the JDK, .NET and the filesystem.
			val base = if (source.length <= 220 && source.all { it.code < 128 }) source else "source"
			var index = usedNames.putIfAbsent(base, 1) ?: return base
			var candidate: String
			do { candidate = "${base}__${++index}" } while (usedNames.putIfAbsent(candidate, 1) != null)
			usedNames[base] = index
			return candidate
		}
		// Per-FILE resilience: an unexpected exception while emitting ONE file must NOT abort the whole loop. The loop
		// walks `moduleFragment.files` in order, so a raw throw (e.g. a mis-shaped-call NPE) silently dropped EVERY file
		// after the offender — the stdlib build lost ~120 type-defs (Sequence/PrimitiveIterators/Continuation/… all live
		// after Maps.kt) yet still reported "success" because the earlier files had already written BIR. Catch, report the
		// crash as a compile ERROR (so the build fails loudly + names the file), and continue so the remaining files still
		// emit and the failure is a single clear diagnostic instead of a catastrophic cascade.
		var crashed = false
		for (irFile in moduleFragment.files) {
			try {
				val path = File(irFile.fileEntry.name).canonicalPath
				val birFile = bir.emitFile(irFile, headers.aliases[path].orEmpty())
				if (birFile != null) {
					val baseName = artifactStem(File(irFile.fileEntry.name).name.removeSuffix(".kt"))
					val sources = sourceCallables?.invoke(irFile, bir::declarationIdForPhysicalAllocation).orEmpty()
					kotc.bir.BirFileCodec.write(File(outputDir, "$baseName.bir.${format.extension}"),
						birFile.copy(sourceCallables = sources.takeIf { it.isNotEmpty() }, dslScopes = headers.scopes[path]), format)
				}
			} catch (e: Throwable) {
				crashed = true
				messageCollector?.report(
					org.jetbrains.kotlin.cli.common.messages.CompilerMessageSeverity.ERROR,
					"BIR emit crashed for ${irFile.fileEntry.name}: ${e.javaClass.simpleName}: ${e.message}")
			}
		}

		// An unsupported construct was reported (with source location), or a file crashed -> fail the compile here, so the
		// build stops with a clear diagnostic instead of producing BIR that crashes ilemit downstream.
		return ClrBackendArtifact(if (bir.hadError || crashed) ExitCode.COMPILATION_ERROR else ExitCode.OK, configuration)
}
