// Producer source for the migrated il-c1net case (C-1 façade-free .NET consumption). Generic methods, params/vararg,
// .NET default args, op_* operators, a struct value-type with instance methods, and C#-origin extension methods on a
// primitive and a reference receiver. Own namespace so the many colliding simple names coexist in this producer.
namespace C1Net {
    public struct Vec2 {
        public int X; public int Y;
        public Vec2(int x, int y) { X = x; Y = y; }
        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 a, int k) => new Vec2(a.X * k, a.Y * k);
        public static Vec2 operator /(Vec2 a, int k) => new Vec2(a.X / k, a.Y / k);
        public static Vec2 operator -(Vec2 a) => new Vec2(-a.X, -a.Y);
        public int Mag2() => X * X + Y * Y;
    }
    public static class Ext {
        public static int tripled(this int n) => n * 3;
        public static string shout(this string s) => s + "!";   // extension on a non-primitive receiver
    }
    public static class Util {
        public static T Echo<T>(T x) => x;                       // generic method (caller-side)
        public static int Sum(params int[] xs) { int s = 0; foreach (var x in xs) s += x; return s; }  // params
        public static int AddDef(int a, int b = 10) => a + b;    // .NET default argument
    }
}
