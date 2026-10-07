package roundtrip.innerfactorysignatures

open class FactoryOwner<T>(private val value: T) {
    inner class Entry {
        private val label: String
        constructor(value: Int) { label = "int:$value" }
        constructor(value: String) { label = "string:$value" }
        fun render(): String = "$value/$label"
    }

    inner class GenericEntry<E>(private val item: E) {
        fun render(): String = "$value/$item"
    }

    inner class DefaultEntry(private val label: String = "default") {
        fun render(): String = "$value/$label"
        fun outerValue(): T = value
    }
}
