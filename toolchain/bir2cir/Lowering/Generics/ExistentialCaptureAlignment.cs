using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// A compiler-generated capture class is physical CLR storage: each capture value, constructor parameter, backing field,
// and field read must agree on one exact representation. FBoundStarProjectionErasure can turn a captured successful
// smart-cast value into a non-generic existential carrier after closure/SAM synthesis copied the Kotlin constructed
// type into a generated class, or before suspend-lambda lowering copies its capture declaration into a state machine.
// Leaving that earlier type on the field makes newobj unverifiable (`G$star` on the stack, `G<T>` in the descriptor).
//
// Align only a carrier whose trusted local/reference metadata names the capture field's original generic classifier.
// The generated declaration and its construction node are the complete authority; no function names, class
// layout guesses, or old artifact spellings participate. The ordinary ExistentialReceiverBinding pass subsequently
// binds calls through the retyped field to the exact carrier slot and preserves their semantic result with an explicit
// projection. Ordinary erased generic values use the same storage rule: copying a source T into a generated
// field must not recover the caller's closed CLR T after the value was deliberately boxed/erased.
static partial class ExistentialCaptureAlignment
{
    static bool TrySemanticOwner(string physical,
        IReadOnlyDictionary<string, string> semanticByPhysical, ReferenceMetadataIndex refs,
        out string semantic)
    {
        if (semanticByPhysical.TryGetValue(physical, out semantic)) return true;
        return refs.TryExistentialSemanticOwner(physical, out semantic);
    }
}
