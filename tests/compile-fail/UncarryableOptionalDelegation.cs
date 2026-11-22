namespace UncarryableOptionalDelegation;

public class Base<T> where T : struct
{
    public Base(T? input, [System.Runtime.InteropServices.Optional] object value) { }
    private Base(T input, object value = null) { }
}
