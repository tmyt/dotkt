// #25 RESIDUAL — CLR PLATFORM fragment. A same-bare-NAME sibling `arrOfNulls` in a DIFFERENT package (so a
// DIFFERENT file class `GenovAltKt`), unreferenced by the app. Its mere presence puts the bare name `arrOfNulls`
// under TWO file-class owners in bir2cir's ref index (which keys top-level statics by bare name). A sig-less
// GENERIC call then has an empty receiver-key that matches neither owner -> TryResolveTopLevelStatic returns false
// -> the generic common-fragment call was left un-promoted. Different package = the frontend never confuses the two
// (the app explicitly imports the common-fragment overload); the collision is purely in the by-name ref index.
package kotlinx.genovalt

fun arrOfNulls(dummy: Int): Int = dummy
