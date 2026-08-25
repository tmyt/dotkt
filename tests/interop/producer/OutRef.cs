// Producer source for the migrated il-outref case (#52). out/ref parameters and a ref-returning method, exercised
// from Kotlin via byref / value-copy / live-ref. Own namespace (OutRef) — the case's original `namespace P`
// collided with netattr/selfref once they share this single producer assembly.
namespace OutRef {
    public class Calc {
        public bool TryDivide(int a, int b, out int quotient) {
            if (b == 0) { quotient = 0; return false; }
            quotient = a / b;
            return true;
        }
        public void Swap(ref int x, ref int y) { var t = x; x = y; y = t; }
        public int SwapWithMarker(ref int x, ref int y, int marker = 7) { Swap(ref x, ref y); return marker; }
        private int[] data = new int[] { 10, 20, 30 };
        public ref int Slot(int i) => ref data[i];   // ref return
    }
}
