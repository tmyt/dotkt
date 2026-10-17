// Producer source for the migrated il-netenum case. A .NET type that is IEnumerable<T> (like a BCL collection /
// LINQ result) but NOT a Kotlin collection: `for (x in it)` must enumerate via GetEnumerator/MoveNext/Current.
// Own namespace (Kfc).
using System.Collections;
using System.Collections.Generic;
using System;
namespace Kfc {
    public sealed class EnumerationProbe<T>(T[] values) : IEnumerable<T> {
        public int Sources { get; private set; }
        public int Enumerators { get; private set; }
        public int Moves { get; private set; }
        public int Currents { get; private set; }
        public int Disposals { get; private set; }
        public bool FailMove { get; set; }
        public InvalidOperationException Failure { get; } = new("native-enumeration-failure");
        public IEnumerable<T> Source() { Sources++; return this; }
        public IEnumerator<T> GetEnumerator() { Enumerators++; return new Cursor(this, values); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Cursor(EnumerationProbe<T> owner, T[] items) : IEnumerator<T> {
            private int index = -1;
            public bool MoveNext() {
                owner.Moves++;
                if (owner.FailMove) throw owner.Failure;
                return ++index < items.Length;
            }
            public T Current {
                get {
                    owner.Currents++;
                    if (index < 0 || index >= items.Length) throw new InvalidOperationException("no-current");
                    return items[index];
                }
            }
            object IEnumerator.Current => Current;
            public void Reset() => throw new NotSupportedException();
            public void Dispose() => owner.Disposals++;
        }
    }
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
