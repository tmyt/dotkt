namespace Bt {
    // A .NET API with UNSIGNED byte surface. facadegen must map System.Byte -> kotlin.UByte and byte[] -> UByteArray
    // (STRICT, #53) — the old collapse to signed Byte/ByteArray lost the unsigned value (200 -> -56).
    public static class B {
        public static byte   One()          => 200;                          // System.Byte  -> kotlin.UByte 200
        public static byte[] Arr()          => new byte[] { 10, 20, 250 };    // System.Byte[]-> kotlin.UByteArray
        public static int    Take(byte x)   => (int)x;                        // consume a UByte
        public static int    TakeArr(byte[] a) => a.Length + (int)a[2];       // consume a UByteArray
    }
}
