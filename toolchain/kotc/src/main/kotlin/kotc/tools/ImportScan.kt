package kotc.tools

import java.io.File
import org.jetbrains.kotlin.cli.common.messages.MessageCollector
import org.jetbrains.kotlin.cli.jvm.compiler.EnvironmentConfigFiles
import org.jetbrains.kotlin.cli.jvm.compiler.KotlinCoreEnvironment
import org.jetbrains.kotlin.com.intellij.openapi.util.Disposer
import org.jetbrains.kotlin.config.CommonConfigurationKeys
import org.jetbrains.kotlin.config.CompilerConfiguration
import org.jetbrains.kotlin.psi.KtPsiFactory

/**
 * `--scan-imports --output <file> <src.kt>...` — extract the .NET-injectable imports from Kotlin sources
 * using the REAL Kotlin PSI parser (not a regex). The facadegen tool reflects over the resulting type list.
 *
 * Why a parser, not a regex: a regex over source text silently drops aliased imports (`import X as Y`),
 * `.*` wildcards, multi-line forms, comments, and backtick-escaped identifiers — all real bugs (interop
 * feedback item 5). [KtImportDirective.importedFqName] returns the canonical FQN with the `as` alias already
 * stripped (the alias is a frontend-binding concern, irrelevant to which .NET type to reflect), and
 * [isAllUnder] flags `.*`. Parsing is purely syntactic, so no classpath/JDK resolution is needed.
 *
 * Output: one entry per line — a fully-qualified type name, or `Namespace.*` for a wildcard import (facadegen
 * expands the latter against the referenced assemblies). Kotlin/Java imports are filtered out here.
 */
object ImportScan {
	fun run(args: Array<String>) {
		val outIdx = args.indexOf("--output")
		require(outIdx >= 0 && outIdx + 1 < args.size) { "--scan-imports requires --output <file>" }
		val outFile = File(args[outIdx + 1])
		val sources = args.drop(outIdx + 2).map(::File).filter { it.isFile }

		val disposable = Disposer.newDisposable("kotc-import-scan")
		try {
			val configuration = CompilerConfiguration().apply {
				put(CommonConfigurationKeys.MESSAGE_COLLECTOR_KEY, MessageCollector.NONE)
				put(CommonConfigurationKeys.MODULE_NAME, "kotc-import-scan")
			}
			val env = KotlinCoreEnvironment.createForProduction(disposable, configuration, EnvironmentConfigFiles.JVM_CONFIG_FILES)
			val factory = KtPsiFactory(env.project)
			val imports = LinkedHashSet<String>()
			for (src in sources) {
				val ktFile = factory.createFile(src.name, src.readText())
				for (directive in ktFile.importDirectives) {
					val fqName = directive.importedFqName?.asString() ?: continue   // null for malformed/error imports
					// Skip stdlib/Java/own-runtime imports — only .NET (and projected) types are injectable. NOTE: `kotlin.`
					// (stdlib) is skipped but `kotlinx.` is NOT — kotlinx-* are external libraries (compiled for the CLR
					// and consumable via a namespace projection), so they must reach facadegen to resolve.
					if (fqName == "kotlin" || fqName.startsWith("kotlin.") || fqName.startsWith("java.") || fqName.startsWith("javax.") || fqName.startsWith("clr.")) continue
					imports.add(if (directive.isAllUnder) "$fqName.*" else fqName)
				}
			}
			outFile.parentFile?.mkdirs()
			outFile.writeText(imports.joinToString("\n"))
		} finally {
			Disposer.dispose(disposable)
		}
	}
}
