namespace Probe {
    public struct Vec2 {
        public int X; public int Y;
        public Vec2(int x, int y) { X = x; Y = y; }
        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public int Mag2() => X * X + Y * Y;
    }
    public static class Util {
        public static T Echo<T>(T x) => x;                       // generic method (caller-side)
        public static int Sum(params int[] xs) { int s = 0; foreach (var x in xs) s += x; return s; }  // params
        public static int AddDef(int a, int b = 10) => a + b;    // .NET default argument
    }
}
