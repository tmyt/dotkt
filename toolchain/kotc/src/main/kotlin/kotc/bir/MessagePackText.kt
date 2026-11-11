package kotc.bir

import org.msgpack.core.MessagePacker

// MessagePack extension 1 preserves lone UTF-16 surrogate units instead of replacing
// them while encoding UTF-8. The logical source value remains a Kotlin String.
internal object MessagePackText {
    fun write(packer: MessagePacker, value: String) {
        if (!LiteralText.requiresCodeUnits(value)) { packer.packString(value); return }
        val bytes = ByteArray(Math.multiplyExact(value.length, 2))
        value.forEachIndexed { index, unit ->
            bytes[index * 2] = unit.code.toByte()
            bytes[index * 2 + 1] = (unit.code ushr 8).toByte()
        }
        packer.packExtensionTypeHeader(1, bytes.size)
        packer.writePayload(bytes)
    }
}
