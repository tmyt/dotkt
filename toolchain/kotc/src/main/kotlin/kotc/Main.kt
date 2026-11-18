package kotc

import kotc.pipeline.ClrCliPipeline
import org.jetbrains.kotlin.cli.common.arguments.K2MetadataCompilerArguments
import org.jetbrains.kotlin.cli.common.arguments.ArgumentParseErrors
import org.jetbrains.kotlin.cli.common.arguments.parseCommandLineArguments
import org.jetbrains.kotlin.cli.common.arguments.preprocessCommandLineArguments
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
	val sdkPluginArgs = "-Xdotkt-plugin-arguments="
	val (pluginFiles, cliArgs) = args.partition { it.startsWith(sdkPluginArgs) }
	val normalizedArgs = cliArgs.filterNot { it == "-no-stdlib" }
	val arguments = parseCommandLineArguments<K2MetadataCompilerArguments>(normalizedArgs)
	// MSBuild already supplies individual items. Read their Kotlin-quoted argument
	// file without applying the CLI's comma-list grammar a second time.
	for (file in pluginFiles) {
		val errors = lazy { ArgumentParseErrors() }
		val tokens = preprocessCommandLineArguments(listOf("@" + file.removePrefix(sdkPluginArgs)), errors).iterator()
		require(!errors.isInitialized() || errors.value.argfileErrors.isEmpty()) { errors.value.argfileErrors.joinToString() }
		val jars = mutableListOf<String>()
		val options = mutableListOf<String>()
		while (tokens.hasNext()) {
			val token = tokens.next()
			when {
				token.startsWith("-Xplugin=") -> jars += token.removePrefix("-Xplugin=")
				token == "-P" -> options += tokens.next()
				else -> error("Unexpected SDK plugin argument: $token")
			}
		}
		arguments.pluginClasspaths += jars
		arguments.pluginOptions += options
	}
	// CLR reference KLIBs represent CLR statics with Kotlin metadata's standard static-member flags. Ignore either
	// user spelling after parsing (including spellings expanded from @argfiles): ClrMetadataConfigurationUpdater
	// installs this as a CLR platform capability, not as a manually selected preview language feature.
	arguments.internalArguments = arguments.internalArguments.filterNot {
		it.languageFeature == org.jetbrains.kotlin.config.LanguageFeature.CompanionBlocksAndExtensions
	}
	arguments.multiPlatform = true
	val collector = PrintingMessageCollector(System.err, MessageRenderer.PLAIN_RELATIVE_PATHS, arguments.verbose)
	val perfManager = PerformanceManagerImpl(CommonPlatforms.defaultCommonPlatform, "Kotlin/CLR compiler")
	val exitCode = ClrCliPipeline(perfManager).execute(arguments, Services.EMPTY, collector)
	System.err.println("kotc finished: $exitCode")
	// Propagate the compiler's exit code to the process, so a COMPILATION_ERROR (e.g. an unsupported construct
	// reported with source location) stops the MSBuild/CLI pipeline before ilemit runs on partial output.
	if (exitCode.code != 0) kotlin.system.exitProcess(exitCode.code)
}
