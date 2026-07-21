// Producer source for the migrated il-injuint case. A .NET API with UNSIGNED parameters (like WinUI's
// Bootstrap.Initialize(uint majorMinorVersion)): System.UInt32 -> kotlin.UInt, System.UInt64 -> kotlin.ULong.
// Own namespace (Boot) so its simple names coexist with the other migrated cases in this single producer assembly.
namespace Boot {
    public static class Strap {
        public static int  Initialize(uint majorMinor) => (int)majorMinor;   // System.UInt32 -> kotlin.UInt
        public static long Big(ulong x) => (long)(x + 1UL);                   // System.UInt64 -> kotlin.ULong
    }
}
