package fieldowner

import kotlin.clr.ClrField

open class FieldBase<T>(seed: T) {
    @ClrField var value: T = seed
    lateinit var text: String
}
open class FieldMiddle<A, B>(seed: B) : FieldBase<B>(seed)

class StaticFieldOwner {
    companion { lateinit var text: String }
}
