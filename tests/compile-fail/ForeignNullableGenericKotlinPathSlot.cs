using System;
using System.Collections.Generic;

// The uninhabitable slot the deriving Kotlin class reaches only THROUGH a Kotlin interface of its own. The graph a
// class inherits runs through this compilation's declarations as freely as through referenced ones, so a walk that
// stopped at the first locally-declared supertype never reached this member.
namespace plainnet
{
    public interface ITakeThrough { string Take(List<int?> xs); }
}
