namespace UncarryableOptionalDefault;

public sealed class Cases
{
    public int Read([System.Runtime.InteropServices.Optional] object value) => value is null ? 0 : 1;
}
