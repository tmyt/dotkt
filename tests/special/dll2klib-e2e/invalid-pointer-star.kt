package invalidpointerstar

import kotlin.clr.ClrPointer

fun invalid(value: ClrPointer<*>): ClrPointer<*> = value
