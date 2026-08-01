using System;
using System.Collections.Generic;

// A CONCRETE virtual property whose type crosses. Unlike the abstract twin, nothing obliges a deriving type to fill
// this one — so the refusal has to reach it through "does this type actually override it", and for a getter that
// question is decided on a ZERO-parameter signature. The sibling property is the control: overriding it is an
// ordinary program and is driven in tests/interop.
namespace plainnet
{
    public class PropBase
    {
        public virtual List<int?> Items { get { return new List<int?>(); } }

        public virtual string Tag { get { return "net"; } }
    }
}
