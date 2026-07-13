// #157: a facadegen-injected GENERIC .NET class (`Cell<T>`) whose ctor param is an un-annotated type
// variable (`T v` -> facadegen meta `oblivious(Tv)`), consumed by a NON-generic .NET extension pinned to
// a CONCRETE instantiation (`Peek(this Cell<int>)`). Inferring `Cell(40)` must yield `Cell<int32>` (NOT
// `Cell<Nullable<int32>>`): the oblivious type-variable ctor param must resolve to a bare `T`, not bias the
// argument to a nullable value type. A `Cell<Nullable<int32>>` receiver is layout-incompatible with Peek's
// invariant `Cell<int32>` slot (no reference conversion) -> Peek reads `c.V` off a `Nullable<int32>` field
// (HasValue byte + value) as garbage instead of the stored 40, returning 2 rather than 41.
namespace Interop
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
