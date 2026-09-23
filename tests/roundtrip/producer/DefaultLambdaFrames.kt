package roundtrip.defaultlambdaframes

class DefaultOwner<T>(val value: T) {
    fun read(block: () -> T = { value }): T = block()
    fun nested(block: () -> () -> T = { { value } }): T = block()()
    fun <M> method(value: M, block: () -> M = { value }): M = block()
    inline fun inlineRead(transform: (T) -> T, block: () -> T = { value }): T = transform(block())
}
