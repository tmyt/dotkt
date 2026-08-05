import EventDelegation.IInheritedEventSource

// Kept in a sibling source file from its use-site test: bir2cir's delegated-event owner relation is module-wide.
// IInheritedEventSource<T> inherits the actual event slot from IEventSource<T>, exercising external interface traversal.
open class DelegatingEventSource<Ignored, T>(source: IInheritedEventSource<T>) : IInheritedEventSource<T> by source {
    // A user overload with the CLR accessor stem must not be mistaken for the synthesized one-parameter shell.
    fun add_Changed(): Int = 42
}

// Inherited use proves the module index carries the physical event owner through the local base-type instantiation.
class DerivedDelegatingEventSource<Ignored, T>(source: IInheritedEventSource<T>) :
    DelegatingEventSource<Ignored, T>(source)
