package kotc.pipeline

import org.jetbrains.kotlin.cli.common.arguments.K2MetadataCompilerArguments
import org.jetbrains.kotlin.cli.common.messages.GroupingMessageCollector
import org.jetbrains.kotlin.cli.common.messages.MessageCollector
import org.jetbrains.kotlin.cli.pipeline.ArgumentsPipelineArtifact
import org.jetbrains.kotlin.com.intellij.openapi.util.Disposer
import org.jetbrains.kotlin.config.AnalysisFlag
import org.jetbrains.kotlin.config.AnalysisFlags
import org.jetbrains.kotlin.config.LanguageFeature
import org.jetbrains.kotlin.config.LanguageVersionSettings
import org.jetbrains.kotlin.config.LanguageVersionSettingsImpl
import org.jetbrains.kotlin.config.Services
import org.jetbrains.kotlin.config.languageVersionSettings
import org.jetbrains.kotlin.platform.CommonPlatforms
import org.jetbrains.kotlin.util.PerformanceManagerImpl
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class ClrUnsignedOptInConfigurationTest {
    private val unsignedOptIn = "kotlin.ExperimentalUnsignedTypes"

    @Test
    fun unsignedArraysAreAnImplicitClrPlatformOptIn() {
        val configured = configure(LanguageVersionSettingsImpl.DEFAULT)
        assertEquals(listOf(unsignedOptIn), configured.getFlag(AnalysisFlags.optIn))
        assertTrue(LanguageVersionSettingsImpl.DEFAULT.getFlag(AnalysisFlags.optIn).isEmpty())
    }

    @Test
    fun platformOptInPreservesUserSettingsAndIsIdempotent() {
        val original = LanguageVersionSettingsImpl(
            LanguageVersionSettingsImpl.DEFAULT.languageVersion,
            LanguageVersionSettingsImpl.DEFAULT.apiVersion,
            mapOf<AnalysisFlag<*>, Any>(
                AnalysisFlags.optIn to listOf("example.OtherPreview", "example.OtherPreview"),
                AnalysisFlags.skipMetadataVersionCheck to true,
            ),
            mapOf(LanguageFeature.CompanionBlocksAndExtensions to LanguageFeature.State.DISABLED),
        )
        val configured = configure(original)
        val twice = configure(configured)
        assertEquals(listOf("example.OtherPreview", "example.OtherPreview", unsignedOptIn),
            configured.getFlag(AnalysisFlags.optIn))
        assertEquals(configured.getFlag(AnalysisFlags.optIn), twice.getFlag(AnalysisFlags.optIn))
        assertEquals(listOf("example.OtherPreview", "example.OtherPreview"), original.getFlag(AnalysisFlags.optIn))
        assertTrue(configured.getFlag(AnalysisFlags.skipMetadataVersionCheck))
        assertEquals(original.languageVersion, configured.languageVersion)
        assertEquals(original.apiVersion, configured.apiVersion)
        assertEquals(original.getCustomizedLanguageFeatures(), configured.getCustomizedLanguageFeatures())
        assertEquals(original.isPreRelease(), configured.isPreRelease())
        assertTrue(configured.supportsFeature(LanguageFeature.CompanionBlocksAndExtensions))
    }

    @Test
    fun explicitUnsignedOptInRemainsUnchanged() {
        val original = LanguageVersionSettingsImpl(
            LanguageVersionSettingsImpl.DEFAULT.languageVersion,
            LanguageVersionSettingsImpl.DEFAULT.apiVersion,
            mapOf(AnalysisFlags.optIn to listOf("example.OtherPreview", unsignedOptIn)),
        )
        assertEquals(original.getFlag(AnalysisFlags.optIn), configure(original).getFlag(AnalysisFlags.optIn))
    }

    private fun configure(settings: LanguageVersionSettings): LanguageVersionSettings {
        val root = Disposer.newDisposable()
        try {
            val messages = GroupingMessageCollector(MessageCollector.NONE, false, false)
            val arguments = K2MetadataCompilerArguments().apply { destination = "unused-test-output" }
            val input = ArgumentsPipelineArtifact(arguments, Services.EMPTY, root, messages,
                PerformanceManagerImpl(CommonPlatforms.defaultCommonPlatform, "CLR unsigned opt-in configuration test"))
            input.configuration.languageVersionSettings = settings
            ClrMetadataConfigurationUpdater.fillConfiguration(input, input.configuration)
            return input.configuration.languageVersionSettings
        } finally {
            Disposer.dispose(root)
        }
    }
}
