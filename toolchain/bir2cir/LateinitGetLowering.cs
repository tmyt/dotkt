// `lateinitGet` is the Kotlin null-check fact. Resolve its failure path to an ordinary constructor expression here,
// where Kotlin semantics become a concrete CLR representation. The nested `new` then follows the same exact local or
// referenced constructor-binding pipeline as every source construction; ilemit only evaluates and throws that CIR.
static partial class LateinitGetLowering { }
