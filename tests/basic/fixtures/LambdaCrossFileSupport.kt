// LambdaTests SIBLING file — the "file B" halves of the multi-file cases il-mfclosure / il-mflambda. Keeping these
// decls in a SEPARATE file of the SAME battery assembly preserves the exact two-file subject the cases guarded:
// each file emits/lifts its OWN synthetic closure/lambda types, and those must not collide across files in the one
// linked assembly (per-file synthetic-name prefixing; per-file lifted-lambda state reset). Consumed by
// LambdaTests.multiFileClosure / multiFileLambda. Names use feature stems and remain assembly-unique.

// ---- il-mfclosure : file-B half — a capturing closure + ref cell for the captured `var flag` --------------------
fun multiFileClosureApplyB(f: () -> Int): Int = f()
fun multiFileClosureFromB(): Int { var flag = false; return multiFileClosureApplyB({ flag = true; if (flag) 20 else 0 }) }

// ---- il-mflambda : file-B half — lifts its own lambda into THIS file class (via multiFileLambdaRunB) -------------------------
fun multiFileLambdaRunB(f: () -> Unit) { f() }
