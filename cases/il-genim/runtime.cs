namespace P {
    // A generic method declared ON an interface (AutoMapper IMapper.Map<T>, MediatR IMediator.Send<T> shape).
    public interface IConv { U Convert<U>(object o); }
    public class Conv : IConv { public U Convert<U>(object o) => (U)o; }
}
