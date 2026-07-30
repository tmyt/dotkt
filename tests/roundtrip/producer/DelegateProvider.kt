package roundtrip.delegateprovider

import kotlin.reflect.KProperty

class Cell<T>(initial: T) {
    private var value: T = initial

    operator fun getValue(thisRef: Any?, property: KProperty<Any?>): T = value
    operator fun setValue(thisRef: Any?, property: KProperty<Any?>, newValue: T) {
        value = newValue
    }
}
