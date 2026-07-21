// Producer source for the migrated il-ifacechainvt case (#129). A base-interface CHAIN: IMid<T> : IBase<T>.
// Implementing IMid<Int> in Kotlin must surface IBase<T>'s member through the super chain with T substituted to the
// VALUE TYPE `int` (bare int32 slots, not Nullable<int>) across the transitively-inherited base link. Own namespace.
namespace Ifacechainvt
{
    public interface IBase<T> { T Get(); }
    public interface IMid<T> : IBase<T> { int Rank(T v); }
}
