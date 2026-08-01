using System;
using System.Collections.Generic;

// TWO REAL SLOTS, ONE PHYSICAL KOTLIN SIGNATURE. The erased image of `Take(List<int?>)` is `Take(List<object>)`,
// and here a sibling declares that outright — so both Kotlin overrides, `Take(xs: List<Int?>)` and
// `Take(ys: List<Any?>)`, emit the same CLR member. Only the second one legitimately fills a slot; the first
// belongs to the `List<int?>` one, which no Kotlin body can fill, and the CLR would bind it to the `object` slot
// while `Take(List<int?>)` kept the base implementation — a silently wrong answer rather than a diagnostic.
//
// The accepted half is driven in tests/interop (KotlinImageSiblingOverride), which is what keeps this refusal from
// swinging back into rejecting the sibling override.
namespace plainnet
{
    public class ImageSiblingBase
    {
        public virtual string Take(List<int?> xs) => "net-q";

        public virtual string Take(List<object> ys) => "net-o";
    }
}
