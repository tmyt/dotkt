// Assignability must survive a non-constructible base: TextBox -> Frame -> Element, where Element has no accessible
// no-arg ctor (a WinRT-style base, like WinUI UIElement). The base edge is emitted for is-a even though the injected
// façade ctor can't chain to `: super()`.
import Kfc.Element
import Kfc.TextBox
import Kfc.Api
fun main() {
    val tb = TextBox()
    println(Api.place(tb))   // placed:0  (TextBox passed where Element is expected)
}
