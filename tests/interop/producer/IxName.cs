// Producer source for the migrated il-ixname case. A custom-named indexer: [IndexerName("Cell")] renames the
// accessors to get_Cell/set_Cell and stamps the type's DefaultMemberAttribute to "Cell" (NOT the standard "Item").
// bir2cir.NetInteropBinding.DefaultIndexerAccessor must read that DefaultMember to bind `g[i]`/`g[i]=v` to
// get_Cell/set_Cell rather than the hardcoded get_Item/set_Item. Own namespace (PIx).
namespace PIx {
    public class Grid {
        private readonly int[] _v = { 10, 20, 30 };
        [System.Runtime.CompilerServices.IndexerName("Cell")]
        public int this[int i] { get => _v[i]; set => _v[i] = value; }
    }
}
