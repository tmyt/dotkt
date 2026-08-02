using System;
using System.Collections.Generic;

// The crossing slot on a GENERIC .NET base, beside a parameter typed by the base's own type variable. Reflection
// hands back the OPEN declaration (`Put(!0, List<int?>)`); the Kotlin class derives from `GBase<String>` and states
// `Put(String, List<object>)`. Compared open, the two disagree at the type variable, the override is not recognised
// as filling the slot, and the class compiles clean and dies at load.
namespace plainnet
{
    public class GBase<T>
    {
        public virtual string Put(T tag, List<int?> values) => "net";
    }
}
