package roundtriptests.collectionfamily

import NUnit.Framework.TestAttribute
import roundtrip.collectionfamily.*

class ReifiedCollectionIdentityTests {
    @TestAttribute fun importedInlineFunctionsRetainCollectionFamilies() {
        val value: Any = mutableSetOf(7)
        check(collectionFamilyIs<Collection<*>>(value))
        check(collectionFamilyIs<MutableCollection<*>>(value))
        check(collectionFamilyIs<Set<*>>(value))
        check(collectionFamilyIs<MutableSet<*>>(value))
        check(collectionFamilySafe<Collection<*>>(value) === value)
        check(collectionFamilyCast<MutableCollection<*>>(value) === value)
        check(collectionFamilySafe<Set<*>>(value) === value)
        check(collectionFamilyCast<MutableSet<*>>(value) === value)
    }

    @TestAttribute fun importedInlineFunctionsRejectUnrelatedValues() {
        val value: Any = Any()
        check(!collectionFamilyIs<Collection<*>>(value))
        check(!collectionFamilyIs<MutableCollection<*>>(value))
        check(!collectionFamilyIs<Set<*>>(value))
        check(!collectionFamilyIs<MutableSet<*>>(value))
        check(collectionFamilySafe<Collection<*>>(value) == null)
        check(collectionFamilySafe<MutableCollection<*>>(value) == null)
        check(collectionFamilySafe<Set<*>>(value) == null)
        check(collectionFamilySafe<MutableSet<*>>(value) == null)
        var rejected = false
        try { collectionFamilyCast<Set<*>>(value) } catch (_: ClassCastException) { rejected = true }
        check(rejected)
    }

    @TestAttribute fun importedInlineFunctionsRetainNullability() {
        check(collectionFamilyIs<Collection<*>?>(null))
        check(collectionFamilyIs<MutableCollection<*>?>(null))
        check(collectionFamilyIs<Set<*>?>(null))
        check(collectionFamilyIs<MutableSet<*>?>(null))
        check(!collectionFamilyIs<Collection<*>>(null))
        check(!collectionFamilyIs<MutableCollection<*>>(null))
        check(!collectionFamilyIs<Set<*>>(null))
        check(!collectionFamilyIs<MutableSet<*>>(null))
        check(collectionFamilyCast<Set<*>?>(null) == null)
    }
}
