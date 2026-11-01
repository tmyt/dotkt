using System.Collections;
using System.Collections.Generic;

namespace ReadOnlyCursorInterop;

public sealed class GrowingReadOnlyFaces : ArrayList, IReadOnlyList<int>, IReadOnlyList<string?>
{
    private readonly List<int> numbers = [1];
    private readonly List<string?> texts = ["first"];

    public GrowingReadOnlyFaces() { Add("raw-a"); Add("raw-b"); Add("raw-c"); }
    public void AddNumber(int value) => numbers.Add(value);
    public void AddText(string? value) => texts.Add(value);
    public void ClearNumbers() => numbers.Clear();
    public void ClearTexts() => texts.Clear();
    int IReadOnlyCollection<int>.Count => numbers.Count;
    int IReadOnlyCollection<string?>.Count => texts.Count;
    int IReadOnlyList<int>.this[int index] => numbers[index];
    string? IReadOnlyList<string?>.this[int index] => texts[index];
    IEnumerator<int> IEnumerable<int>.GetEnumerator() => throw new System.InvalidOperationException("must use the selected indexer");
    IEnumerator<string?> IEnumerable<string?>.GetEnumerator() => throw new System.InvalidOperationException("must use the selected indexer");
    public override IEnumerator GetEnumerator() => throw new System.InvalidOperationException("must not enumerate raw storage");
}
