package kotc.bir

import java.io.File
import org.jetbrains.kotlin.config.CompilerConfigurationKey
import org.msgpack.core.MessagePack

enum class BirFileFormat(val extension: String) { JSON("json"), MESSAGE_PACK("msgpack") }

internal object BirFileCodec {
    val outputFormat = CompilerConfigurationKey.create<BirFileFormat>("BIR output format")
    private val header = "DOTKT\u0000BIR\u0000MSGPACK\u0000".toByteArray(Charsets.US_ASCII)

    fun encode(value: BirFileDeclaration): ByteArray = MessagePack.newDefaultBufferPacker().use { packer ->
        BirMessagePack.write(value, packer)
        header + packer.toByteArray()
    }

    fun write(path: File, value: BirFileDeclaration, format: BirFileFormat) {
        when (format) {
            BirFileFormat.JSON -> path.writeText(buildString { value.writeJson(this) })
            BirFileFormat.MESSAGE_PACK -> path.writeBytes(encode(value))
        }
    }
}
