namespace P
{
    // A .NET type with `out` and `ref` parameters, exercised from Kotlin via __clrout/__clrref markers.
    public class Calc
    {
        public bool TryDivide(int a, int b, out int quotient)
        {
            if (b == 0) { quotient = 0; return false; }
            quotient = a / b;
            return true;
        }

        public void Swap(ref int x, ref int y) { var t = x; x = y; y = t; }
    }
}
