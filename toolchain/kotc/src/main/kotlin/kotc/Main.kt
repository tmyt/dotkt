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
	// `--scan-imports --output <file> <src.kt>...` — a pre-compile subcommand that extracts the .NET-injectable
	// imports with the real Kotlin PSI parser (the metadata pre-step facadegen consumes). Reuses this same jar/
	// launcher so no extra distribution is needed; returns before the normal compile path.
	if (args.firstOrNull() == "--scan-imports") {
		kotc.tools.ImportScan.run(args)
		return
	}
	// CLR reference KLIBs use Kotlin metadata's standard static-member flags. Upstream
	// currently gates deserialized static declarations as companion-block members, so the
	// embedded CLR compiler enables that language capability unconditionally. Keep this
	// platform policy here, at the single compiler entrypoint, rather than duplicating an
	// experimental switch across MSBuild, scripts, and user projects.
	val staticFeature = "-XXLanguage:+CompanionBlocksAndExtensions"
	val normalizedArgs = args
		.filterNot {
			it == "-no-stdlib" ||
				it.startsWith("-XXLanguage:+CompanionBlocksAndExtensions") ||
				it.startsWith("-XXLanguage:-CompanionBlocksAndExtensions")
		} + staticFeature
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
