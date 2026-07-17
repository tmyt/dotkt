// issue #44: a generic .NET method whose SIBLING param is a facadegen-injected interop type —
// JsonSerializer.Serialize<TValue>(TValue, JsonSerializerOptions?). bir2cir's ShapeSynthesis used to
// erase the JsonSerializerOptions leaf to "Object" (absent from the ref.dll alias index + PrimShapeName),
// so the shapes ["gp","Object"] mismatched ilemit's reflected ["gp","JsonSerializerOptions"] and the
// prefix-equality overload filter yielded zero candidates -> ilemit `cands.First()` threw
// "Sequence contains no elements". Now ShapeSynthesis resolves the interop leaf off the refs to its
// .NET simple name, so the shapes match and the generic overload binds.
import System.Text.Json.JsonSerializer
import System.Text.Json.JsonSerializerOptions

fun main() {
    val opts = JsonSerializerOptions()
    opts.WriteIndented = false
    println(JsonSerializer.Serialize<Int>(42, opts))
    println(JsonSerializer.Serialize<String>("hi", opts))
}
