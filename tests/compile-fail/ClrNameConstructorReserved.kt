class ClrNameConstructorReserved {
    @kotlin.clr.ClrName(".ctor")
    fun ordinaryMethod(): Unit = Unit
}

fun main() = ClrNameConstructorReserved().ordinaryMethod()
