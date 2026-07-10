@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.reflect

/**
 * A name-only [KProperty] materialization (#70) used in two ways.
 *
 * (a) **Directly**, as the delegate-property convention's compiler-synthesized `property` argument — the 2nd
 * argument of `getValue(thisRef, property)`/`setValue(thisRef, property, value)`/`provideDelegate`. Kotlin's own
 * delegate convention never surfaces this argument by any identity other than `.name` in an ordinary body
 * (`Delegates.observable`'s `afterChange(property, old, new)`, `Property should be initialized before get:
 * ${property.name}`, …) — `get()`/`set()`/`invoke()` are never called on it, which is why this stub does not
 * implement `KProperty0`/`KProperty1`.
 *
 * (b) As the **base class** of kotc's `propertyRef` lift for a genuine callable reference (`::prop`, `obj::p`,
 * `Type::p`). That lift is a `KProperty0`/`KMutableProperty0`/`KProperty1`/`KMutableProperty1` subclass which
 * adds the `get`/`set`/`invoke` slots and inherits `name` + `annotations` from here (it passes the property
 * name to this ctor), rather than hand-synthesizing them.
 */
public open class ClrPropertyStub<out V>(override val name: String) : KProperty<V> {
    override val annotations: List<Annotation> get() = emptyList()
}
