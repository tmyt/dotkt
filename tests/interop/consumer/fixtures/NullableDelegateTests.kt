// C#-producer roundtrip consumer battery B — NULLABLE .NET DELEGATE interop (#150). Consumes the NRT-ENABLED
// producer (../producer-nrt): DlgNrt.Api carries real [Nullable] delegate-type-arg bytes so dll2klib surfaces
// Func<string?>/Action<string?> as nullable Kotlin lambda return/param.
//   delegnull <- il-delegnull  a lambda returning null binds Func<string?> only when the return surfaces as
//                              String?; an Action<string?> param is String?, so `s ?: "<n>"` is legal.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import DlgNrt.Api

class NullableDelegateTests {
    @TestAttribute
    fun delegnull() {
        // Func<string?> return: the lambda body returns null — compiles only when the return surfaces as String?.
        assertEquals("<null>", Api.RunNullable { null })   // <null>
        assertEquals("hello", Api.RunNullable { "hello" }) // hello
        // Func<string> return: non-null result.
        assertEquals("world", Api.RunNonNull { "world" })  // world
        // Action<string?> param: `s` is String?, so the null-coalescing is legal and meaningful.
        val collected = mutableListOf<String>()
        Api.Consume { s -> collected.add(s ?: "<n>") }
        assertEquals("<n>", collected[0])  // <n> — Consume passed null first
        assertEquals("x", collected[1])    // x   — then "x"
    }
}
