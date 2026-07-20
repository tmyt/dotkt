// Cross-file companion declarations for the MigratedM5 batteries — their use sites live in a SIBLING .kt of the
// SAME battery assembly (MigratedM5LanguageTests.kt), preserving the original cases' cross-file (and, for
// il-xfaceimpl, cross-NAMESPACE) dimension. Keeping these here is not cosmetic: it is the exact shape the old
// cases proved.
//   il-xfaceimpl  -> M5IfaceC/M5ImplC/m5cur/m5call : a class INSTANTIATED + virtually dispatched from a DIFFERENT
//                    file than its interface/class, IN A NAMESPACE (package m5p). Regressed when ilemit's
//                    interface-link pass looked types up by simple name (Impl) while _types was keyed by the BIR
//                    name (m5p.M5ImplC) -> KeyNotFound at FindMethod. The dispatch must reach M5ImplC.go.
//   il-xprop      -> m5counter/m5bump : a mutable top-level property declared in one file, read + written from
//                    another file, must resolve to THIS file's static (not the reading file's class).
// All top-level names are M5-prefixed (one assembly = one namespace) and the file is packaged (m5p) to keep the
// cross-namespace dimension intact.
package m5p

interface M5IfaceC { fun go(x: Int): Int }
class M5ImplC : M5IfaceC { override fun go(x: Int): Int = x }
var m5cur: M5IfaceC? = null
fun m5call(x: Int): Int = m5cur?.go(x) ?: -1

var m5counter = 0
fun m5bump() { m5counter = m5counter + 1 }
