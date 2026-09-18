import NUnit.Framework.TestAttribute

private class EmptyOverrideList : List<Int> by listOf(7, 9) {
    var calls = 0
    override fun isEmpty(): Boolean { calls++; return true }
}
private class EmptyOverrideMutableList : MutableList<Int> by mutableListOf(7, 9) {
    var calls = 0
    override fun isEmpty(): Boolean { calls++; return true }
}

class ProjectedIsEmptyOverrideTests {
    @TestAttribute fun kotlinReadOnlyOverrideWinsOverNonZeroCount() {
        val list = EmptyOverrideList()
        val value: Any = list
        check(list.size == 2)
        check((value as List<*>).isEmpty())
        check((value as Collection<*>).isEmpty())
        check(list.calls == 2)
    }

    @TestAttribute fun kotlinMutableOverrideWinsOverNonZeroCount() {
        val list = EmptyOverrideMutableList()
        val value: Any = list
        check(list.size == 2)
        check((value as MutableList<*>).isEmpty())
        check((value as MutableCollection<*>).isEmpty())
        check(list.calls == 2)
    }
}
