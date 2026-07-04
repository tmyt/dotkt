// try-EXPRESSION used as a VALUE in an OPERAND slot: `1 + try{..}`, `"x" + try{..}`, `n * try{..}`.
// Kotlin: `try` IS an expression. On the CLR a protected (try) region must be ENTERED WITH AN EMPTY
// evaluation stack — `leave` clears the stack — so a value-producing try nested in an operand slot
// (where the left operand is already pushed) cannot run inline. kotc already emits the correct
// value-form: a `valueBlock` = [ var tmp; try{ ..setLocal tmp.. } catch{ ..setLocal tmp.. } ] with
// result=local(tmp). The remaining step — hoisting that try-bearing valueBlock OUT of the operand
// slot to a preceding temp (preserving left-to-right eval order) so the region runs with an empty
// stack — is CLR eval-order normalization and belongs to bir2cir (kotc has no CLR-stack knowledge).
fun risky(): Int = "5".toInt()

fun main() {
    val n = "n=" + try { "5".toInt() } catch (e: NumberFormatException) { -1 }
    println(n)                                                          // n=5
    println(1 + try { risky() } catch (e: Exception) { 0 })            // 6
    println("bad=" + try { "x".toInt() } catch (e: Exception) { -1 })   // bad=-1
    println(10 + try { 20 } finally { })                               // 30  (try/finally as operand)
}
