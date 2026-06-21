namespace P
{
    // out/ref parameters and a ref-returning method, exercised from Kotlin via byref / value-copy / live-ref.
    public class Calc
    {
        public bool TryDivide(int a, int b, out int quotient)
        {
            if (b == 0) { quotient = 0; return false; }
            quotient = a / b;
            return true;
        }

        public void Swap(ref int x, ref int y) { var t = x; x = y; y = t; }

        private int[] data = new int[] { 10, 20, 30 };
        public ref int Slot(int i) => ref data[i];   // ref return
    }
}
