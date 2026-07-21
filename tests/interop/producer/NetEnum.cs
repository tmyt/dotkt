// Producer source for the migrated il-netenum case. A .NET type that is IEnumerable<T> (like a BCL collection /
// LINQ result) but NOT a Kotlin collection: `for (x in it)` must enumerate via GetEnumerator/MoveNext/Current.
// Own namespace (Kfc).
using System.Collections;
using System.Collections.Generic;
namespace Kfc {
    public class Nums : IEnumerable<int> {
        private readonly int[] _xs = new[] { 10, 20, 30 };
        public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)_xs).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _xs.GetEnumerator();
    }
    public class Words : IEnumerable<string> {
        private readonly List<string> _w = new() { "a", "bb", "ccc" };
        public IEnumerator<string> GetEnumerator() => _w.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _w.GetEnumerator();
    }
}
