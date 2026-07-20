// Producer source for the migrated il-delegnull case (#150). Compiled with C# NRT ENABLED (this csproj sets
// <Nullable>enable</Nullable>) so these delegate type-args carry real [Nullable] bytes:
//   Func<string?>   -> [1,2] (Func non-null, result nullable)
//   Action<string?> -> [1,2] (Action non-null, arg nullable)
//   Func<string>    -> no [Nullable] (uniform non-null) -> non-null via NullableContext.
// facadegen surfaces the nullable arg/return as Kotlin String?, so a lambda returning null into Func<string?>
// (or reading a String? Action param) compiles only when the byte is honored. Own namespace (DlgNrt).
using System;
namespace DlgNrt {
    public static class Api {
        // A lambda returning null binds here ONLY IF facadegen surfaces the return as Kotlin `String?` (#150).
        public static string RunNullable(Func<string?> f) => f() ?? "<null>";
        // A non-null Func<string> return: the lambda must produce a non-null String.
        public static string RunNonNull(Func<string> f) => f();
        // An Action<string?>: the lambda param is `String?`, so `s ?: "<n>"` is legal and meaningful.
        public static void Consume(Action<string?> a) { a(null); a("x"); }
    }
}
