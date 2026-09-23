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

private class ProtectedVirtualChild<T>(initial: T, private var current: T) : VirtualIndexers.ProtectedGrid<T>(initial) {
    protected override operator fun get(key: Int): T = current
    protected override operator fun set(key: Int, value: T) { current = value }
}

private class NominalVirtualChild : VirtualIndexers.NominalGrid() {
    override operator fun get(key: Int): VirtualIndexers.ResultDerived = VirtualIndexers.ResultDerived()
}

private class ObjectVirtualChild : VirtualIndexers.ObjectGrid() {
    override operator fun get(key: Int): String = "child"
}

private class ClosedVirtualGrid : VirtualIndexers.GenericGrid<String, Int>(17) {
    private var stored = 31
    override operator fun get(key: String, column: Int): Int = stored
    override operator fun set(key: String, column: Int, value: Int) { stored = value }
}

private interface IntermediateVirtualGrid<T> : VirtualIndexers.IGrid<T>
private class ClosedVirtualInterfaceGrid : IntermediateVirtualGrid<Int> {
    private var stored = 19
    override operator fun get(key: Int): Int = stored
    override operator fun set(key: Int, value: Int) { stored = value }
}

class VirtualIndexerTests {
    @TestAttribute
    fun protectedAndCovariantAccessorsOccupyExactlyOneBaseSlot() {
        val protected = ProtectedVirtualChild(17, 31)
        check(protected.Read(1) == 31)
        protected.Write(1, 53)
        check(protected.Read(1) == 53)
        val nominal: VirtualIndexers.NominalGrid = NominalVirtualChild()
        check(nominal[1] is VirtualIndexers.ResultDerived)
        val objectBase: VirtualIndexers.ObjectGrid = ObjectVirtualChild()
        check(objectBase[1] == "child")
    }

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
        val closed: VirtualIndexers.GenericGrid<String, Int> = ClosedVirtualGrid()
        check(closed["row", 1] == 31)
        closed["row", 1] = 61
        check(closed["row", 1] == 61)
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
        val closed: VirtualIndexers.IGrid<Int> = ClosedVirtualInterfaceGrid()
        check(closed[1] == 19)
        closed[1] = 43
        check(closed[1] == 43)
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
