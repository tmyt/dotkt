// Intentionally sorts before ZComparableBase.kt.  The final interface-slot manifest must be independent of source
// file order when the base receives its non-generic System.IComparable bridge during late CLR synthesis.
class CrossFileComparableDerived(n: Int) : CrossFileComparableBase(n)
