// Producer source for the migrated il-csext case (#137, Avalonia report B). Genuine C#-origin `[Extension]` statics
// that facadegen must surface as Kotlin extension functions so a Kotlin/clr app can call them façade-free via
// `import Csext.*` (the `using Csext;` analog). Own namespace.
namespace Csext
{
    public class W
    {
        public int N;
        public W() { }
        public W(int n) { N = n; }
    }

    public class Box<T>
    {
        public T Value;
        public Box() { }
        public Box(T v) { Value = v; }
    }

    public static class Ext
    {
        // Non-generic extension, no extra params -> `fun W.Twice(): Int`.
        public static int Twice(this W w) => w.N * 2;
        // Extension with an extra value param -> `fun W.PlusN(k: Int): Int`.
        public static int PlusN(this W w, int k) => w.N + k;
        // Generic extension: the receiver is `Box<T>` over the method's own type param -> `fun <T> Box<T>.Echo(): T`.
        public static T Echo<T>(this Box<T> b) => b.Value;
    }
}
