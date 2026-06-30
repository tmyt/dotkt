package kotlin.coroutines

// Forces generation of coroutines.kotlin_builtins (the builtin package fragment for kotlin.coroutines),
// mirroring upstream libraries/stdlib/jvm/builtins/Coroutines.kt. Required so -Xoutput-builtins-metadata
// does not crash on a missing kotlin.coroutines fragment.
private fun hackToForceKotlinBuiltinsForKotlinCoroutinesPackage() {}
