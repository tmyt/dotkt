namespace UncarryableEnumOptionalDefault;

public enum Choice
{
    None = 0,
}

public static class Cases
{
    public static int Read(
        [System.Runtime.InteropServices.Optional]
        [System.Runtime.CompilerServices.DecimalConstant(0, 0, 0, 0, 2)] Choice value) => 0;
}
