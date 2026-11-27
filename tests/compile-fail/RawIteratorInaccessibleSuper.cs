using System.Collections;

namespace RawIteratorInaccessibleSuper;

public class RawBase : IEnumerable {
    IEnumerator IEnumerable.GetEnumerator() => System.Array.Empty<object>().GetEnumerator();
}
