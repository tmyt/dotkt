@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.reflect

/**
 * A name-only [KProperty] materialization (#70) for a delegated-property accessor's compiler-synthesized
 * `property` argument (`getValue(thisRef, property)`/`setValue(thisRef, property, value)`/`provideDelegate`).
 * Kotlin's own delegate convention never surfaces this argument by any identity other than `.name` in an
 * ordinary body (`Delegates.observable`'s `afterChange(property, old, new)`, `Property should be initialized
 * before get: ${property.name}`, …) — `get()`/`set()`/`invoke()` are never called on it, so this stub does not
 * implement `KProperty0`/`KProperty1`. A genuine callable reference (`::prop`) materializes a REAL
 * `KProperty0`/`KMutableProperty0`/`KProperty1`/`KMutableProperty1` implementation instead (kotc's
 * `propertyRef`), never this stub.
 */
public class ClrPropertyStub<out V>(override val name: String) : KProperty<V> {
    override val annotations: List<Annotation> get() = emptyList()
}
