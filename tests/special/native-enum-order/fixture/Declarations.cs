namespace MetadataOrder;
public enum Code { Hidden__ = 400, First = 103, Second = -7 }
public static class Api
{
    public static Code First() => Code.First;
    public static Code Hidden() => Code.Hidden__;
    public static Code Unknown() => (Code)777;
    public static int Bits(Code value) => (int)value;
}
