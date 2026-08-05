// Ktp inbox battery — migrates the single-project cases/ktproj-inbox .ktproj sample onto the in-process NUnit suite.
// #37 finding 3 (catalog-first, TPA-fallback): framework/inbox types that are NOT copy-local (absent from
// @(ReferenceCopyLocalPaths), so absent from ilemit's runtime catalog) — System.Text.Json.JsonSerializerOptions +
// System.Net.Http.HttpClient — must still resolve via the fallback onto ilemit's own host framework (TPA). Before the
// fix these hard-failed "cannot resolve .NET type". Any net10.0 test project already has these inbox in its shared
// framework and takes NO PackageReference, so the case migrates as a plain fixture importing them façade-free.
//
// Coverage preserved (old case -> method):
//   ktproj-inbox  -> inbox_tpaFallbackFrameworkTypes  System.Text.Json (inbox) + System.Net.Http (inbox) resolve via TPA
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse
import System.Text.Json.JsonSerializerOptions
import System.Net.Http.HttpClient

class FrameworkTypeResolutionTests {
    @TestAttribute
    fun tpaFallbackFrameworkTypes() {
        val opts = JsonSerializerOptions()
        assertFalse(opts.WriteIndented)                     // indented False — System.Text.Json (inbox) resolves via TPA
        val http = HttpClient()
        assertEquals(100, http.Timeout.TotalSeconds.toInt())  // timeout 100 — System.Net.Http (inbox) resolves; default 100s
    }
}
