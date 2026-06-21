namespace P {
    public class Engine { public Widget makeWidget() => new Widget(); }   // returns another .NET type (cross-type)
    public class Widget { public int value() => 42; }
    public static class Arr {
        public static int[] range3() => new[] { 10, 20, 30 };              // array return
        public static int sumArr(int[] a) { int s = 0; foreach (var x in a) s += x; return s; }  // array param
        public static string[] words() => new[] { "a", "b", "c" };
    }
}
