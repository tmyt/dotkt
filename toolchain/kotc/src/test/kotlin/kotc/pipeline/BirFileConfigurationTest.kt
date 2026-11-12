package kotc.pipeline

import kotc.bir.BirFileCodec
import kotc.bir.BirFileFormat
import org.jetbrains.kotlin.cli.common.arguments.K2MetadataCompilerArguments
import org.jetbrains.kotlin.cli.common.messages.GroupingMessageCollector
import org.jetbrains.kotlin.cli.common.messages.MessageCollector
import org.jetbrains.kotlin.cli.pipeline.ArgumentsPipelineArtifact
import org.jetbrains.kotlin.com.intellij.openapi.util.Disposer
import org.jetbrains.kotlin.config.LanguageVersionSettingsImpl
import org.jetbrains.kotlin.config.Services
import org.jetbrains.kotlin.config.languageVersionSettings
import org.jetbrains.kotlin.platform.CommonPlatforms
import org.jetbrains.kotlin.util.PerformanceManagerImpl
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class BirFileConfigurationTest {
    @Test
    fun outputDefaultsToJsonAndHonorsTheExplicitCodec() {
        for ((options, expected) in listOf(
            emptyList<String>() to BirFileFormat.JSON,
            listOf("plugin:dotkt:bir-format=json") to BirFileFormat.JSON,
            listOf("plugin:dotkt:bir-format=msgpack") to BirFileFormat.MESSAGE_PACK,
        )) {
            val (format, errors) = configure(options)
            assertEquals(expected, format)
            assertFalse(errors)
        }
    }

    @Test
    fun duplicateOrUnknownFormatsAreConfigurationErrors() {
        for (options in listOf(
            listOf("plugin:dotkt:bir-format=invalid"),
            listOf("plugin:dotkt:bir-format="),
            listOf("plugin:dotkt:bir-format=json", "plugin:dotkt:bir-format=json"),
            listOf("plugin:dotkt:bir-format=json", "plugin:dotkt:bir-format=msgpack"),
        )) {
            val (format, errors) = configure(options)
            assertNull(format)
            assertTrue(errors)
        }
    }

    private fun configure(options: List<String>): Pair<BirFileFormat?, Boolean> {
        val root = Disposer.newDisposable()
        try {
            val messages = GroupingMessageCollector(MessageCollector.NONE, false, false)
            val arguments = K2MetadataCompilerArguments().apply {
                destination = "unused-test-output"
                pluginOptions = options.toTypedArray()
            }
            val input = ArgumentsPipelineArtifact(arguments, Services.EMPTY, root, messages,
                PerformanceManagerImpl(CommonPlatforms.defaultCommonPlatform, "BIR format configuration test"))
            input.configuration.languageVersionSettings = LanguageVersionSettingsImpl.DEFAULT
            ClrMetadataConfigurationUpdater.fillConfiguration(input, input.configuration)
            return input.configuration.get(BirFileCodec.outputFormat) to messages.hasErrors()
        } finally {
            Disposer.dispose(root)
        }
    }
}
