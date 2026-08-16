// Producer source for the migrated il-genim case. A generic method declared ON an interface (AutoMapper
// IMapper.Map<T>, MediatR IMediator.Send<T> shape); the implementing class is assignable to the interface.
// Own namespace.
namespace GenIm {
    public interface IConv { U Convert<U>(object o); }
    public class Conv : IConv { public U Convert<U>(object o) => (U)o; }

    // Same name and parameter vector, distinguished only by method generic arity. A generic external call must
    // consume the frontend-selected generic slot instead of treating both declarations as candidates.
    public interface IArityOverload {
        string Pick(int value);
        string Pick<T>(int value);
    }
    public class ArityOverload : IArityOverload {
        public string Pick(int value) => "plain:" + value;
        public string Pick<T>(int value) => typeof(T).Name + ":" + value;
    }
}
