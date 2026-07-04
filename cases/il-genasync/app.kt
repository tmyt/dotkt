// Genuine-async isolation rung: a suspend fun that truly suspends on a real .NET Task
// (Task.Delay), resumes on the threadpool, and is drained by blockOn.
import System.Threading.Tasks.Task
import kotlin.clr.await
import dotkt.support.blockOn

suspend fun f(): Int {
    Task.Delay(1).await()   // genuine suspension on a real .NET async
    return 7
}

fun main() {
    println(blockOn { f() })   // 7
}
