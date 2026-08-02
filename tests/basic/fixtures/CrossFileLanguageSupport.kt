// Cross-file companion declarations for the language fixtures — their use sites live in a SIBLING .kt of the
// SAME suite assembly (RuntimeTypesVisibilityAndCrossFileTests.kt), preserving the original cases' cross-file (and, for
// il-xfaceimpl, cross-NAMESPACE) dimension. Keeping these here is not cosmetic: it is the exact shape the old
// cases proved.
//   il-xfaceimpl  -> CrossFileLanguageInterface/CrossFileLanguageImplementation/crossFileLanguageCurrent/crossFileLanguageCall : a class INSTANTIATED + virtually dispatched from a DIFFERENT
//                    file than its interface/class, IN A NAMESPACE (package crossFileLanguage). Regressed when ilemit's
//                    interface-link pass looked types up by simple name (Impl) while _types was keyed by the BIR
//                    name (crossFileLanguage.CrossFileLanguageImplementation) -> KeyNotFound at FindMethod. The dispatch must reach CrossFileLanguageImplementation.go.
//   il-xprop      -> crossFileLanguageCounter/crossFileLanguageBump : a mutable top-level property declared in one file, read + written from
//                    another file, must resolve to THIS file's static (not the reading file's class).
// All top-level names are CrossFileLanguage-prefixed (one assembly = one namespace) and the file is packaged (crossFileLanguage) to keep the
// cross-namespace dimension intact.
package crossFileLanguage

interface CrossFileLanguageInterface { fun go(x: Int): Int }
class CrossFileLanguageImplementation : CrossFileLanguageInterface { override fun go(x: Int): Int = x }
var crossFileLanguageCurrent: CrossFileLanguageInterface? = null
fun crossFileLanguageCall(x: Int): Int = crossFileLanguageCurrent?.go(x) ?: -1

var crossFileLanguageCounter = 0
fun crossFileLanguageBump() { crossFileLanguageCounter = crossFileLanguageCounter + 1 }
