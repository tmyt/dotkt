package roundtrip.utf16literals

const val HIGH_CHAR: Char = '\ud800'
const val LOW_TEXT: String = "\udfff"

class Utf16ConstantOwner {
    companion object {
        const val TEXT: String = "\ud800\u0000\udfff\ud83d\ude00"
    }
}

fun readConstantText(): String = LOW_TEXT
fun directChar(): Char = '\udbff'
fun directText(): String = "\udc00X\ud800\ud83d\ude00"
inline fun inlineChar(): Char = '\ud800'
inline fun inlineText(): String = "\udfff\ud800"
fun defaultChar(value: Char = '\udbff'): Char = value
fun defaultText(value: String = "\udc00"): String = value
