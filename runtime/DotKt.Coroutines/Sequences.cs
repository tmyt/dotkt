// `sequence { yield(…) }` — a restricted-suspension (multi-shot) coroutine producing a lazy Sequence<T>, mapped
// to a lazy .NET IEnumerable<T>. The compiler lowers the block to a state machine implementing the trivial
// ISeqStep<T> (MoveNext advances to the next yield, Current holds it); `Seq.Of` wraps it into IEnumerable<T> so
// the awkward IEnumerator<T> dual-interface boilerplate stays in C#, not in emitted IL. See docs §13h / #42.
using System.Collections.Generic;

namespace DotKt.Coroutines
{
    /// The minimal step protocol a compiler-generated sequence state machine implements.
    public interface ISeqStep<out T>
    {
        bool MoveNext();
        T Current { get; }
    }

    public static class Seq
    {
        /// Wrap a sequence state machine as a lazy IEnumerable<T> (the C# iterator supplies IEnumerable/IEnumerator).
        public static IEnumerable<T> Of<T>(ISeqStep<T> step)
        {
            while (step.MoveNext()) yield return step.Current;
        }
    }
}
