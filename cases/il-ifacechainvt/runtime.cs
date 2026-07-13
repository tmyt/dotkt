namespace ChainNs
{
    // A base-interface CHAIN: IMid<T> : IBase<T>. Implementing IMid<Int> in Kotlin must surface IBase<T>'s member
    // through the super chain with T substituted to the VALUE TYPE `int` (bare int32 slots, not Nullable<int>) — the
    // #128 value-type-generic-interface fix must hold across the transitively-inherited base-interface link (#129).
    public interface IBase<T> { T Get(); }
    public interface IMid<T> : IBase<T> { int Rank(T v); }
}
