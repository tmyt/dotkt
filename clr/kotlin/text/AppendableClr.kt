@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.text

public actual interface Appendable {
    public actual fun append(value: Char): Appendable
    public actual fun append(value: CharSequence?): Appendable
    public actual fun append(value: CharSequence?, startIndex: Int, endIndex: Int): Appendable
}
