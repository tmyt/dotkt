using System;
namespace P
{
    // .NET Span<int> APIs, fed a stack buffer via StackBuffer.asSpan().
    public static class SpanOps
    {
        public static int Sum(Span<int> s) { int t = 0; foreach (var x in s) t += x; return t; }
        public static void Fill(Span<int> s, int v) { for (int i = 0; i < s.Length; i++) s[i] = v; }
    }
}
