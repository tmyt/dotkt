class ExportedVirtualIndexer : VirtualIndexers.Grid() {
    override operator fun get(key: Int): Int = 100 + super.get(key)
    override operator fun set(key: Int, value: Int) { super.set(key, value + 200) }
}

class ExportedProtectedIndexer : VirtualIndexers.ProtectedGrid<Int>(17) {
    private var current = 31
    protected override operator fun get(key: Int): Int = current
    protected override operator fun set(key: Int, value: Int) { current = value }
}

class ExportedCovariantIndexer : VirtualIndexers.NominalGrid() {
    override operator fun get(key: Int): VirtualIndexers.ResultDerived = VirtualIndexers.ResultDerived()
}
