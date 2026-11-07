package kotc.pipeline

import org.jetbrains.kotlin.cli.common.arguments.K2MetadataCompilerArguments
import org.jetbrains.kotlin.cli.common.messages.GroupingMessageCollector
import org.jetbrains.kotlin.cli.common.messages.MessageCollector
import org.jetbrains.kotlin.cli.pipeline.ArgumentsPipelineArtifact
import org.jetbrains.kotlin.com.intellij.openapi.util.Disposer
import org.jetbrains.kotlin.config.LanguageVersionSettingsImpl
import org.jetbrains.kotlin.config.Services
import org.jetbrains.kotlin.config.languageVersionSettings
import org.jetbrains.kotlin.config.zipFileSystemAccessor
import org.jetbrains.kotlin.io.ZipFileSystemInPlaceAccessor
import org.jetbrains.kotlin.platform.CommonPlatforms
import org.jetbrains.kotlin.util.PerformanceManagerImpl
import java.nio.file.Files
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNotSame
import kotlin.test.assertSame
import kotlin.test.assertTrue

class KlibZipCacheConfigurationTest {
	@Test
	fun configuredAccessorIsInstalledAndOwnedByCompilationRoot() {
		val archive = Files.createTempFile("dotkt-klib-cache-", ".zip")
		try {
			ZipOutputStream(Files.newOutputStream(archive)).use {
				it.putNextEntry(ZipEntry("entry"))
				it.write(42)
			}
			for (limit in listOf(null, "256")) {
				val root = Disposer.newDisposable()
				val fileSystem = try {
					val accessor = configure(limit, root)
					assertNotSame(ZipFileSystemInPlaceAccessor, accessor)
					accessor.withZipFileSystem(archive) { it }.also { assertTrue(it.isOpen) }
				} finally {
					Disposer.dispose(root)
				}
				assertFalse(fileSystem.isOpen, "the configured accessor must belong to the compilation root")
			}
		} finally {
			Files.deleteIfExists(archive)
		}
	}

	@Test
	fun zeroLimitInstallsTheUncachedAccessor() {
		val root = Disposer.newDisposable()
		try {
			assertSame(ZipFileSystemInPlaceAccessor, configure("0", root))
		} finally {
			Disposer.dispose(root)
		}
	}

	private fun configure(limit: String?, root: org.jetbrains.kotlin.com.intellij.openapi.Disposable)
		: org.jetbrains.kotlin.io.ZipFileSystemAccessor {
		val arguments = K2MetadataCompilerArguments().apply {
			destination = "unused-test-output"
			if (limit != null) klibZipFileAccessorCacheLimit = limit
		}
		val input = ArgumentsPipelineArtifact(arguments, Services.EMPTY, root,
			GroupingMessageCollector(MessageCollector.NONE, false, false),
			PerformanceManagerImpl(CommonPlatforms.defaultCommonPlatform, "KLIB cache configuration test"))
		input.configuration.languageVersionSettings = LanguageVersionSettingsImpl.DEFAULT
		ClrMetadataConfigurationUpdater.fillConfiguration(input, input.configuration)
		return assertNotNull(input.configuration.zipFileSystemAccessor)
	}
}
