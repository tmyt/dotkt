// .NET Task interop battery — migrates the SYNCHRONOUS (`.Result`/`.Wait()`, no suspend)
// cases/il-task* onto the in-process NUnit suite. These exercise the seam that lets Kotlin BUILD a Task<T>
// from a .NET generic static factory and unwrap it synchronously — NOT the coroutine cold-core, so they
// migrate normally (not frozen). The Interop consumer project's reference KLIBs expose the .NET Task
// types from `import System.Threading.Tasks.*`. Each old case's `main` + stdout-golden becomes one
// @TestAttribute method preserving every asserted value 1:1 (see the `// <expected>` comments).
//
// Coverage preserved (old case -> method):
//   il-taskgen  -> taskgen_genericStaticFactory   Task.FromResult<TResult>: the direct generic static resolves FromResult(42) -> Task<Int>
//   il-taskwhen -> taskwhen_nestedGenericCombinators  Task.WhenAny -> Task<Task<T>> (double-nested return) + Task.WhenAll(vararg Task<T>) -> Task<T[]>
//
// The fixture introduces no shared top-level declarations.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Threading.Tasks.Task
import System.Threading.Tasks.Task1

class TaskInteropTests {
    // il-taskgen: `Task.FromResult(42)` binds as `FromResult<Int>(42): Task<Int>` — the async-interop enabler.
    @TestAttribute
    fun genericStaticFactory() {
        val t: Task1<Int> = Task.FromResult(42)
        assertEquals(42, t.Result)   // 42
    }

    // il-taskwhen: `Task.WhenAny(a,b): Task<Task<Int>>` (double-nested RETURN) unwraps outer-then-inner; the
    // sibling `Task.WhenAll(vararg Task<Int>): Task<Int[]>` unwraps the array and sums it.
    @TestAttribute
    fun nestedGenericCombinators() {
        val a = Task.FromResult(10)
        val b = Task.FromResult(20)

        val any = Task.WhenAny(a, b)
        any.Wait()
        assertEquals(10, any.Result.Result)   // 10

        // WhenAll over a vararg of Task<Int> -> Task<Int[]>; unwrap the array and sum it.
        val all = Task.WhenAll(Task.FromResult(1), Task.FromResult(2), Task.FromResult(3))
        all.Wait()
        val r = all.Result                    // Int[]
        assertEquals(6, r[0] + r[1] + r[2])   // 6
    }
}
