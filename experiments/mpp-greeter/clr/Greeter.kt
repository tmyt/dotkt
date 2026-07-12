package mpp.greeter

// CLR ACTUAL. In DotKt the CLR is the ONLY platform, so this is simply "the implementation".
// (No "Clr" qualifier is needed on the user surface — a single-target toolchain has nothing to
// distinguish it from. The directory name `clr/` is the internal fragment tag, not a user concept.)
actual class Greeter {
    actual fun say(): String = "Hello from the CLR actual"
}
