// Producer source for the migrated il-outref case (#52). out/ref parameters and a ref-returning method, exercised
// from Kotlin via byref / value-copy / live-ref. Own namespace (OutRef) — the case's original `namespace P`
// collided with netattr/selfref once they share this single producer assembly.
namespace OutRef {
    public class NativeBase {
        public NativeBase(int value) { Value = value; }
        public int Value { get; }
    }
    public sealed class NativeBound<T> where T : NativeBase {
        public NativeBound(T value) { Value = value; }
        public T Value { get; }
    }
    public sealed class ReferenceBox<T> where T : class {
        public ReferenceBox(T value) { Value = value; }
        public T Value { get; }
    }
    public abstract class NativeApi {
        public abstract int Read(NativeBase value);
        public static int Invoke(NativeApi api, NativeBase value) => api.Read(value);
    }
    public static class NativeSlots {
        public static void Replace<T>(ref ReferenceBox<T> slot, ReferenceBox<T> replacement) where T : class => slot = replacement;
        public static bool Read<T>(NativeBound<T> value, out T result) where T : NativeBase {
            result = value.Value;
            return true;
        }
    }
    public interface ICounter { int Apply(int value); int Read(); }
    public struct Counter : ICounter {
        private int total;
        public Counter(int initial) => total = initial;
        public int Apply(int value) => total += value;
        public int Read() => total;
    }
    public class Calc {
        public static string SelfOrder = "";
        public int SelfCalls;
        public Calc Self() { SelfCalls++; SelfOrder += "r"; return this; }
        public bool TryDivide(int a, int b, out int quotient) {
            if (b == 0) { quotient = 0; return false; }
            quotient = a / b;
            return true;
        }
        public void Swap(ref int x, ref int y) { var t = x; x = y; y = t; }
        public int SwapWithMarker(ref int x, ref int y, int marker = 7) { Swap(ref x, ref y); return marker; }
        public int ReadWithDefaults(ref int value, int omitted = 7, int later = 0) => value;
        private int[] data = new int[] { 10, 20, 30 };
        public ref int Slot(int i) => ref data[i];   // ref return
    }
}
