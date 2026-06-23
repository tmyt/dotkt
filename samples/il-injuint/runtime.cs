namespace Boot {
    // A .NET API with UNSIGNED parameters (like WinUI's Bootstrap.Initialize(uint majorMinorVersion)).
    public static class Strap {
        public static int  Initialize(uint majorMinor) => (int)majorMinor;   // System.UInt32 -> kotlin.UInt
        public static long Big(ulong x) => (long)(x + 1UL);                   // System.UInt64 -> kotlin.ULong
    }
}
