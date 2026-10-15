import kotlin.clr.ClrField

class ExplicitLateinit<T : Any> {
    @ClrField lateinit var value: T
}
