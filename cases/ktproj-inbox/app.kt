// #37 finding 3: a framework/inbox type that is NOT copy-local (absent from @(ReferenceCopyLocalPaths), so
// absent from ilemit's runtime catalog) must still resolve — via the TPA fallback onto ilemit's own host
// framework. Before the fix these hard-failed with "cannot resolve .NET type" (the removed AppDomain fallback
// used to resolve System.Text.Json by luck). No PackageReference: both types are inbox in the shared framework.
import System.Text.Json.JsonSerializerOptions
import System.Net.Http.HttpClient

fun main() {
    val opts = JsonSerializerOptions()
    println("indented " + opts.WriteIndented)     // System.Text.Json (inbox) resolves
    val http = HttpClient()
    println("timeout " + http.Timeout.TotalSeconds.toInt())   // System.Net.Http (inbox) resolves; default 100s
}
