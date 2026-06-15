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
	val collector = PrintingMessageCollector(
		System.err,
		MessageRenderer.PLAIN_RELATIVE_PATHS,
		arguments.verbose,
	)
	val perfManager = PerformanceManagerImpl(JvmPlatforms.defaultJvmPlatform, "Kotlin/CLR compiler")

	val exitCode = ClrCliPipeline(perfManager).execute(arguments, Services.EMPTY, collector)
	System.err.println("clrc finished: $exitCode")
}
