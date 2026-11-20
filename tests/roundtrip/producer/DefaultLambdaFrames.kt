package roundtrip.defaultlambdaframes

fun interface DefaultSupplier<T> { fun read(): T }

private fun defaultFrameLabel(label: String, configure: StringBuilder.() -> Unit = {}): String =
    StringBuilder(label).apply(configure).toString()

class DefaultOwner<T>(val value: T) {
    val constructedLabel = defaultFrameLabel("ctor")
    fun memberLabel() = defaultFrameLabel("member")
    fun read(block: () -> T = { value }): T = block()
    fun nested(block: () -> () -> T = { { value } }): T = block()()
    fun <M> method(value: M, block: () -> M = { value }): M = block()
    inline fun inlineRead(transform: (T) -> T, block: () -> T = { value }): T = transform(block())
    fun sam(block: DefaultSupplier<T> = DefaultSupplier { value }): T = block.read()
    inline fun <M> inlineMethod(value: M, transform: (M) -> M, block: () -> M = { value }): M = transform(block())
}
