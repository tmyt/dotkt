namespace P {
    // A user class implementing a user interface -> the class must be assignable to an interface-typed parameter.
    public interface IShape { string Describe(); }
    public class Circle : IShape { public string Describe() => "circle"; }
    public class Square : IShape { public string Describe() => "square"; }
    public class Drawer { public string Draw(IShape s) => "draw:" + s.Describe(); }
}
