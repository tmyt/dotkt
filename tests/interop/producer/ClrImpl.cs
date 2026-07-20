// Producer source for the migrated il-clrimpl case. A user class implementing a user interface -> the class
// must be assignable to an interface-typed parameter. Own namespace to keep these types distinct.
namespace ClrImpl {
    public interface IShape { string Describe(); }
    public class Circle : IShape { public string Describe() => "circle"; }
    public class Square : IShape { public string Describe() => "square"; }
    public class Drawer { public string Draw(IShape s) => "draw:" + s.Describe(); }
}
