@file:Suppress(
    "ACTUAL_WITHOUT_EXPECT",
    "NO_ACTUAL_FOR_EXPECT",
    "UNCHECKED_CAST",
    "NOTHING_TO_INLINE",
    "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS",
    "IMPLEMENTING_FUNCTION_INTERFACE",
)
// Step-1 CLR stub mirroring the JVM `actual` declarations of kotlin.reflect.KProperty.
// Bodies are `TODO` pending the @Clr/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.reflect

public actual interface KProperty<out V> : KCallable<V>

public actual interface KMutableProperty<V> : KProperty<V>

public actual interface KProperty0<out V> : KProperty<V>, () -> V {
    public actual fun get(): V
}

public actual interface KMutableProperty0<V> : KProperty0<V>, KMutableProperty<V> {
    public actual fun set(value: V)
}

public actual interface KProperty1<T, out V> : KProperty<V>, (T) -> V {
    public actual fun get(receiver: T): V
}

public actual interface KMutableProperty1<T, V> : KProperty1<T, V>, KMutableProperty<V> {
    public actual fun set(receiver: T, value: V)
}

public actual interface KProperty2<D, E, out V> : KProperty<V>, (D, E) -> V {
    public actual fun get(receiver1: D, receiver2: E): V
}

public actual interface KMutableProperty2<D, E, V> : KProperty2<D, E, V>, KMutableProperty<V> {
    public actual fun set(receiver1: D, receiver2: E, value: V)
}
