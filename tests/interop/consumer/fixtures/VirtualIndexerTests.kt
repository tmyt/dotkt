import NUnit.Framework.TestAttribute

private class VirtualGridChild : VirtualIndexers.Grid() {
    override operator fun get(key: Int): Int = 100 + super.get(key)
    override operator fun set(key: Int, value: Int) { super.set(key, value + 200) }
    override operator fun get(key: String): Int = 300 + super.get(key)
    override operator fun set(key: String, value: Int) { super.set(key, value + 400) }
}

private class VirtualGenericChild<T>(initial: T, private var current: T) :
    VirtualIndexers.ReorderedGrid<T, String>(initial) {
    override operator fun get(key: String, column: Int): T = current
    override operator fun set(key: String, column: Int, value: T) { current = value }
    fun baseRead(): T = super.get("base", 7)
    fun baseWrite(value: T) { super.set("base", 7, value) }
}

private class VirtualInterfaceGrid<T>(private var stored: T) : VirtualIndexers.IGrid<T> {
    override operator fun get(key: Int): T = stored
    override operator fun set(key: Int, value: T) { stored = value }
}

class VirtualIndexerTests {
    @TestAttribute
    fun overloadedAccessorsDispatchThroughBaseAndSuperStaysNonvirtual() {
        val child = VirtualGridChild()
        val base: VirtualIndexers.Grid = child
        check(child[2] == 119) { "direct getter: ${child[2]}" }
        check(base[2] == 119) { "base getter: ${base[2]}" }
        base[2] = 41
        check(child.Stored == 239) { "int setter stored: ${child.Stored}" }
        check(base[2] == 341) { "int getter after set: ${base[2]}" }
        check(base["abc"] == 542) { "string getter: ${base["abc"]}" }
        base["abc"] = 43
        check(child.Stored == 440) { "string setter stored: ${child.Stored}" }
        check(child["abc"] == 743) { "direct string getter after set: ${child["abc"]}" }
        check(base["abc"] == 743) { "base string getter after set: ${base["abc"]}" }
    }

    @TestAttribute
    fun customNamedGenericSlotsUseTheConstructedAncestorFrame() {
        val child = VirtualGenericChild("base", "child")
        val base: VirtualIndexers.GenericGrid<String, String> = child
        check(base["row", 2] == "child")
        base["row", 2] = "changed"
        check(child["row", 2] == "changed")
        check(child.baseRead() == "base")
        child.baseWrite("base-changed")
        check(child.baseRead() == "base-changed")
        check(base["row", 2] == "changed")
        val integers = VirtualGenericChild(17, 31)
        val integerBase: VirtualIndexers.GenericGrid<String, Int> = integers
        check(integerBase["row", 3] == 31)
        integerBase["row", 3] = 53
        check(integers["row", 3] == 53)
        check(integers.baseRead() == 17)
    }

    @TestAttribute
    fun interfaceIndexersBindBothAccessors() {
        val strings: VirtualIndexers.IGrid<String> = VirtualInterfaceGrid("first")
        check(strings[1] == "first")
        strings[1] = "second"
        check(strings[1] == "second")
        val integers: VirtualIndexers.IGrid<Int> = VirtualInterfaceGrid(19)
        check(integers[2] == 19)
        integers[2] = 37
        check(integers[2] == 37)
    }
}
