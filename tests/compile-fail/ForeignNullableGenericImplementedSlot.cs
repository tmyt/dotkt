using System;
using System.Collections.Generic;

// A PLAIN .NET surface — no DotKt metadata of any kind. That provenance is the point: the carrier machinery that
// repairs an erased slot reads DotKt attributes, so a BCL or third-party interface has nothing for it to read, and
// this whole column fell through to a load-time failure with our type's name on it.
namespace plainnet
{
    // An uninhabitable slot on an INTERFACE. `class C : ITake` used to compile clean and die with
    // "Signature of the body and declaration in a method implementation do not match".
    public interface ITake { string Take(List<int?> xs); }

    // The ABSTRACT BASE twin, which died with "does not have an implementation" instead.
    public abstract class BTake { public abstract string Take(List<int?> xs); }
}
