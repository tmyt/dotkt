// #1: overriding a BCL virtual whose delegate param has an `object`/Any? Invoke arg. facadegen must surface the
// delegate as a function type `(Any?) -> Unit` (NOT collapse the whole delegate to bare Any?), so the natural
// Kotlin override matches — previously `error: 'Post' overrides nothing`.
import Kfc.Ctx

class MyCtx : Ctx() {
    override fun Post(cb: (Any?) -> Unit, state: Any?) {
        cb(state)
    }
}

fun main() {
    val c = MyCtx()
    c.Post({ s -> println("posted: $s") }, 42)   // posted: 42
    (c as Ctx).Post({ s -> println("base-typed: $s") }, 7)  // base-typed: 7 (virtual dispatch through Ctx)
}
