package clrc

import clrc.pipeline.ClrCliPipeline
import org.jetbrains.kotlin.cli.common.arguments.K2JVMCompilerArguments
import org.jetbrains.kotlin.cli.common.arguments.parseCommandLineArguments
import org.jetbrains.kotlin.cli.common.messages.MessageRenderer
import org.jetbrains.kotlin.cli.common.messages.PrintingMessageCollector
import org.jetbrains.kotlin.config.Services
import org.jetbrains.kotlin.platform.jvm.JvmPlatforms
import org.jetbrains.kotlin.util.PerformanceManagerImpl

/**
 * CLI entry. We accept standard kotlinc JVM arguments (-classpath, -d, source roots, ...) so the
 * reused frontend resolves against a real stdlib jar without any custom argument plumbing.
 */
fun main(args: Array<String>) {
	val arguments = parseCommandLineArguments<K2JVMCompilerArguments>(args.toList())
	// Enable expect/actual matching so a library's commonMain + a CLR `actual` source set compile together as one
	// flat module (the pragmatic minimum for building kotlinx libraries — no HMPP/klib). Harmless for single-
	// platform code. See docs/design-coroutines-clr.md §13a (resolution 4).
	arguments.multiPlatform = true
	val collector = PrintingMessageCollector(
		System.err,
		MessageRenderer.PLAIN_RELATIVE_PATHS,
		arguments.verbose,
	)
	val perfManager = PerformanceManagerImpl(JvmPlatforms.defaultJvmPlatform, "Kotlin/CLR compiler")

	val exitCode = ClrCliPipeline(perfManager).execute(arguments, Services.EMPTY, collector)
	System.err.println("clrc finished: $exitCode")
	// Propagate the compiler's exit code to the process, so a COMPILATION_ERROR (e.g. an unsupported construct
	// reported with source location) stops the MSBuild/CLI pipeline before ilemit runs on partial output.
	if (exitCode.code != 0) kotlin.system.exitProcess(exitCode.code)
}
