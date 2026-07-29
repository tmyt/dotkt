// Producer source for the migrated il-genextval case (#157). A reference-KLIB-projected GENERIC `Cell<T>` whose ctor param
// is an un-annotated type variable (`T v` -> `oblivious(Tv)`), consumed by a NON-generic extension pinned to a
// CONCRETE instantiation (`Peek(this Cell<int>)`): inferring `Cell(40)` must yield `Cell<int32>`, not
// `Cell<Nullable<int32>>` (layout-incompatible with Peek's invariant slot). Own namespace.
namespace Genextval
{
    public class Cell<T>
    {
        public T V;
        public Cell(T v) { V = v; }
    }

    public static class CellExt
    {
        // NON-generic extension pinned to `Cell<int>` (invariant): only a `Cell<int32>` receiver binds.
        public static int Peek(this Cell<int> c) => c.V + 1;
    }
}
