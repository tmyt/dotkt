using System;
using System.Collections.Generic;

// A PLAIN .NET surface whose uninhabitable slot is NOT declared on the interface the Kotlin class names. Reflection
// does not hand a derived interface its base's members, so a walk that asked each DIRECT supertype for
// `GetMethods()` saw an empty `IDerived` and let the class through to the load-time failure it exists to prevent.
namespace plainnet
{
    public interface IBase { string Take(List<int?> xs); }

    // The slot is one hop up. `class C : IDerived` must be refused exactly as `class C : IBase` is.
    public interface IDerived : IBase { }
}
