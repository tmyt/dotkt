// MCALL2: a suspend fun in a SECOND source file, called from the first — proves the same-assembly
// cross-file cold rewrite (owner:null callStatic; the global fixpoint spans both files).
suspend fun crossFileVal(): Int = 7
