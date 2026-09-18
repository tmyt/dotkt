import NUnit.Framework.TestAttribute
import CollectionStorageInterop.ReifiedCollectionFaces
import CollectionStorageInterop.ForeignListFaces

private inline fun <reified T> foreignFamilyIs(value: Any?): Boolean = value is T
private inline fun <reified T> foreignFamilySafe(value: Any?): T? = value as? T
private inline fun <reified T> foreignFamilyCast(value: Any?): T = value as T
private inline fun <reified T> foreignFamilyAccepts(value: Any) {
    check(foreignFamilyIs<T>(value))
    check(foreignFamilySafe<T>(value) === value)
    check(foreignFamilyCast<T>(value) === value)
}
private inline fun <reified T> foreignFamilyRejects(value: Any) {
    check(!foreignFamilyIs<T>(value))
    check(foreignFamilySafe<T>(value) == null)
    var rejected = false
    try { foreignFamilyCast<T>(value) } catch (_: ClassCastException) { rejected = true }
    check(rejected)
}

class ReifiedCollectionFacesTests {
    @TestAttribute fun bareEnumerableIsNotACollection() {
        val value = ReifiedCollectionFaces.Enumerable()
        check(value is Iterable<*> && value !is Collection<*> && value !is MutableCollection<*>)
        foreignFamilyAccepts<Iterable<*>>(value)
        foreignFamilyRejects<Collection<*>>(value)
        foreignFamilyRejects<MutableCollection<*>>(value)
        foreignFamilyRejects<Set<*>>(value)
        foreignFamilyRejects<MutableSet<*>>(value)
    }

    @TestAttribute fun genericOnlyCollectionHasCollectionButNotSetIdentity() {
        val value = ReifiedCollectionFaces.Collection()
        check(value is Collection<*> && value is MutableCollection<*>)
        foreignFamilyAccepts<Collection<*>>(value)
        foreignFamilyAccepts<MutableCollection<*>>(value)
        foreignFamilyRejects<Set<*>>(value)
        foreignFamilyRejects<MutableSet<*>>(value)
        check(value as MutableCollection<*> === value)
        check(value as? MutableCollection<*> === value)
        check((value as MutableCollection<*>).size == 2)
    }

    @TestAttribute fun foreignReadOnlyCollectionDoesNotGainMutability() {
        val value = ReifiedCollectionFaces.ReadOnlyCollection()
        check(value is Collection<*> && value !is MutableCollection<*>)
        check(value as? MutableCollection<*> == null)
        foreignFamilyAccepts<Collection<*>>(value)
        foreignFamilyRejects<MutableCollection<*>>(value)
        foreignFamilyRejects<Set<*>>(value)
        foreignFamilyRejects<MutableSet<*>>(value)
    }

    @TestAttribute fun foreignSetHasCollectionAndSetIdentity() {
        val value = ReifiedCollectionFaces.Set()
        check(value is Collection<*> && value is MutableCollection<*>)
        check(value is Set<*> && value is MutableSet<*>)
        foreignFamilyAccepts<Collection<*>>(value)
        foreignFamilyAccepts<MutableCollection<*>>(value)
        foreignFamilyAccepts<Set<*>>(value)
        foreignFamilyAccepts<MutableSet<*>>(value)
        check((value as MutableCollection<*>).size == 2)
    }

    @TestAttribute fun foreignReadOnlySetDoesNotGainMutability() {
        val value = ReifiedCollectionFaces.ReadOnlySet()
        check(value is Collection<*> && value is Set<*>)
        check(value !is MutableCollection<*> && value !is MutableSet<*>)
        foreignFamilyAccepts<Collection<*>>(value)
        foreignFamilyAccepts<Set<*>>(value)
        foreignFamilyRejects<MutableCollection<*>>(value)
        foreignFamilyRejects<MutableSet<*>>(value)
    }

    @TestAttribute fun rawAndGenericListMutableCollectionCastsPreserveIdentityAndCount() {
        for (value in arrayOf(ForeignListFaces.Raw(), ForeignListFaces.Generic())) {
            check(value is MutableCollection<*>)
            check(value as MutableCollection<*> === value)
            check(value as? MutableCollection<*> === value)
            foreignFamilyAccepts<MutableCollection<*>>(value)
            check((value as MutableCollection<*>).size == 2)
        }
    }
}
