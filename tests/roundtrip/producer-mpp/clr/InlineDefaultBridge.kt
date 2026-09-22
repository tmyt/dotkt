package inlinedefaultbridge

inline fun forwarded(receiver: inlinedefaults.Receiver, block: (Int) -> Int): Int = block(receiver.next())
fun outer(receiver: inlinedefaults.Receiver, value: Int = forwarded(receiver) { it }): Int = value
fun lifted(block: () -> Int = { forwarded(inlinedefaults.Receiver()) { it } }): Int = block()
inline fun forwardedCapture(before: () -> Unit): Int {
    before()
    return inlinedefaults.CaptureDefault().read()
}
