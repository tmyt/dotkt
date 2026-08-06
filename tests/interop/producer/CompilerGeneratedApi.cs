using System.Runtime.CompilerServices;

namespace CompilerGeneratedApi;

// CompilerGenerated is an implementation boundary only for trusted DotKt output. Other CLR producers may expose
// public generated declarations as real API (records are a common example), and dll2klib must preserve that surface.
public sealed class Surface
{
    [CompilerGenerated]
    public int Value() => 31;

    [CompilerGenerated]
    public sealed class Nested
    {
        public int Value() => 37;
    }
}
