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

        // generateSequence(seed, next): yield seed, next(seed), … until next returns null. Two variants chosen by
        // the compiler from T's kind, because `(T) -> T?` has different CLR shapes — `Func<T, Nullable<T>>` for a
        // value type vs `Func<T, T>` (T-or-null) for a reference type. (T7's nullable-in-generics, sequence side.)
        public static IEnumerable<T> GenerateRef<T>(T seed, System.Func<T, T> next) where T : class
        {
            for (var c = seed; c != null; c = next(c)) yield return c;
        }
        public static IEnumerable<T> GenerateVal<T>(T? seed, System.Func<T, T?> next) where T : struct
        {
            for (var c = seed; c.HasValue; c = next(c.Value)) yield return c.Value;
        }
        // generateSequence(nextFunction): the seedless form — call next() each step until null.
        public static IEnumerable<T> GenerateRefN<T>(System.Func<T> next) where T : class
        {
            for (var c = next(); c != null; c = next()) yield return c;
        }
        public static IEnumerable<T> GenerateValN<T>(System.Func<T?> next) where T : struct
        {
            for (var c = next(); c.HasValue; c = next()) yield return c.Value;
        }
    }
}
