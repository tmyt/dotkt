package kotc

import kotc.pipeline.ClrCliPipeline
import org.jetbrains.kotlin.cli.common.arguments.K2MetadataCompilerArguments
import org.jetbrains.kotlin.cli.common.arguments.parseCommandLineArguments
import org.jetbrains.kotlin.cli.common.messages.MessageRenderer
import org.jetbrains.kotlin.cli.common.messages.PrintingMessageCollector
import org.jetbrains.kotlin.config.Services
import org.jetbrains.kotlin.platform.CommonPlatforms
import org.jetbrains.kotlin.util.PerformanceManagerImpl

/**
 * CLI entry. We accept the subset of kotlinc-style arguments used by the scripts (-classpath, -d,
 * source roots, ...) and run the common metadata frontend against KLIB dependencies.
 */
fun main(args: Array<String>) {
	val normalizedArgs = args
		.filterNot {
			it == "-no-stdlib" ||
				it.startsWith("-XXLanguage:+CompanionBlocksAndExtensions") ||
				it.startsWith("-XXLanguage:-CompanionBlocksAndExtensions")
		}
	val arguments = parseCommandLineArguments<K2MetadataCompilerArguments>(normalizedArgs)
	arguments.multiPlatform = true
	val collector = PrintingMessageCollector(System.err, MessageRenderer.PLAIN_RELATIVE_PATHS, arguments.verbose)
	val perfManager = PerformanceManagerImpl(CommonPlatforms.defaultCommonPlatform, "Kotlin/CLR compiler")
	val exitCode = ClrCliPipeline(perfManager).execute(arguments, Services.EMPTY, collector)
	System.err.println("kotc finished: $exitCode")
	// Propagate the compiler's exit code to the process, so a COMPILATION_ERROR (e.g. an unsupported construct
	// reported with source location) stops the MSBuild/CLI pipeline before ilemit runs on partial output.
	if (exitCode.code != 0) kotlin.system.exitProcess(exitCode.code)
}
