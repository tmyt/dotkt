using System;
using System.Collections.Generic;

// The same uninhabitable slot behind a PROPERTY. Its CLR member is a virtual `get_Items` marked SpecialName, so a
// refusal that skipped every special-name member never looked at it and a Kotlin property override emitted the
// mismatched slot and died at load. An accessor is a slot like any other.
namespace plainnet
{
    public interface IProp { List<int?> Items { get; } }
}
