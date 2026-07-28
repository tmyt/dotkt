// A suspend function whose body never actually suspends, with a byref-like PARAMETER. It still owes the cold
// entry + public Task bridge ABI, neither of which can carry a `ref struct`, so the refusal is unconditional —
// the same rule C# applies to an async method (CS4012), and the reason a suspend declaration's ABI is checked
// before the "does the body suspend" question is asked. Without it this shape reached run time as
// InvalidProgramException at the cold entry.
import System.Span

suspend fun cfFreeLen(s: Span<Int>): Int = s.Length

suspend fun main() {
    println(cfFreeLen(Span<Int>(arrayOf(1, 2, 3))))
}
