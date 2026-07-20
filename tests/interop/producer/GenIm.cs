// Producer source for the migrated il-genim case. A generic method declared ON an interface (AutoMapper
// IMapper.Map<T>, MediatR IMediator.Send<T> shape); the implementing class is assignable to the interface.
// Own namespace.
namespace GenIm {
    public interface IConv { U Convert<U>(object o); }
    public class Conv : IConv { public U Convert<U>(object o) => (U)o; }
}
