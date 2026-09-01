package invalidpointerfunction

import kotlin.clr.ClrPointer

fun invalid(value: (ClrPointer<Int>) -> Unit): Unit = Unit
