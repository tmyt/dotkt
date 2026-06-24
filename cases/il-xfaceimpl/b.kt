package p
// The impl is INSTANTIATED + virtually dispatched from a DIFFERENT file than its interface/class —
// and in a package (namespace). Regressed: ilemit's interface-link pass looked the type up by simple
// name (Impl) but _types is keyed by the BIR name (p.Impl) -> KeyNotFound at FindMethod.
fun main() { cur = Impl(); call(1) }
