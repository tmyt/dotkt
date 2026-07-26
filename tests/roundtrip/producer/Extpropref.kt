package roundtrip.extpropref

class RefBox<T>(var value: T)

val String.auditLength: Int
    get() = length

val <T> List<T>.auditLast: T
    get() = this[lastIndex]

val <T> T.auditSingleton: List<T>
    get() = listOf(this)

var <T> RefBox<T>.auditValue: T
    get() = value
    set(newValue) {
        value = newValue
    }
