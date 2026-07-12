package mpp.greeter

// COMMON (platform-agnostic) surface: an `expect` declaration with NO body.
// The sole platform — CLR — supplies the `actual`. This is the entire point of the MPP experiment:
// prove that kotc compiles a common+actual pair for the CLR target via the fragment machinery
// (-Xexpect-actual-classes / -Xfragments=common,clr / -Xfragment-refines=clr:common), exactly as the
// stdlib does, but for an ordinary user project (no -Xstdlib-compilation).
expect class Greeter {
    fun say(): String
}
