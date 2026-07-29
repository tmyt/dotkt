// Producer source for the migrated il-csextrecv case (#144). SAME-NAME, SAME-ARITY `[Extension]` methods on DIFFERENT
// receiver types (plain class AND primitive) spread across parallel static classes in ONE namespace (the Avalonia
// `*Extensions` shape) — each must bind to its OWN receiver's static class, keyed by the receiver's classifier ClassId,
// not arity alone. The primitive receivers (`this string`/`this int`) are named bare String/Int by dll2klib; the
// ClassId key reconciles that with the backend's kotlin.String/kotlin.Int. Own namespace.
namespace Csextrecv
{
    public class Foo { public int A; public Foo(int a) { A = a; } }
    public class Bar { public int B; public Bar(int b) { B = b; } }

    public static class FooExt
    {
        public static int Tag(this Foo f) => f.A + 1;         // Foo.Tag() -> A+1
        public static int Mix(this Foo f, int k) => f.A * k;  // same-name/same-arity across Foo/Bar
    }
    public static class BarExt
    {
        public static int Tag(this Bar b) => b.B + 100;       // Bar.Tag() -> B+100
        public static int Mix(this Bar b, int k) => b.B - k;  // same name `Mix`, arity 1, receiver Bar
    }
    // PRIMITIVE receivers: `this string` vs `this int` — same name `Kind`, same arity.
    public static class StrExt { public static int Kind(this string s) => s.Length; }
    public static class IntExt { public static int Kind(this int n) => n + 1000; }
}
