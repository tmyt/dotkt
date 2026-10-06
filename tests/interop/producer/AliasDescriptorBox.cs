using System.Collections.Generic;

namespace Interop.AliasDescriptors;

public sealed class Box<T>
{
    public T Keep(T value, List<string> fixedValues)
    {
        if (fixedValues[0] != "fixed") throw new System.InvalidOperationException();
        return value;
    }

    public int Keep(string tag, List<string> fixedValues) =>
        tag == "tag" && fixedValues[0] == "fixed" ? 99 : -1;

    public U Echo<U>(T anchor, U value) => value;
}
