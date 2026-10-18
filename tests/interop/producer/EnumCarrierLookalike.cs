namespace DotKt.Runtime.CompilerServices
{
    public sealed class KotlinBasicEnumAttribute : System.Attribute { }
}

namespace EnumCarrierLookalike
{
    [DotKt.Runtime.CompilerServices.KotlinBasicEnum]
    public enum Code { First = 103, Negative = -7 }

    public static class Api
    {
        public static Code Negative() => Code.Negative;
        public static int Bits(Code value) => (int)value;
    }
}
