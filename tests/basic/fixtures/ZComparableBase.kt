open class CrossFileComparableBase(val crossFileComparableValue: Int) : Comparable<CrossFileComparableBase> {
    override fun compareTo(other: CrossFileComparableBase): Int =
        crossFileComparableValue - other.crossFileComparableValue
}
