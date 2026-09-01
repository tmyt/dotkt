package invalidpointergeneric

import kotlin.clr.ClrPointer

fun invalid(values: List<ClrPointer<Int>>): Int = values.size
