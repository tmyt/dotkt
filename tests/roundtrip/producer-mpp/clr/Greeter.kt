// ktproj-mpp (#119) CLR ACTUAL. On DotKt the CLR is the only platform, so this is simply "the implementation" that
// the common `expect class Greeter` resolves to via the expect/actual match during the MPP module split.
package mpp.app

actual class Greeter {
    actual fun say(): String = "Hello from the CLR actual"
}
