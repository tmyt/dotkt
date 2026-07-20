// ktproj-mpp (#119) COMMON fragment: an `expect` declaration with NO body. Files under common/ are tagged as the
// common source set by the MPP targets (-Xcommon-sources), gated on <DotKtMultiplatform> (Sdk="DotKt.Sdk.Mpp").
// The sole platform — CLR — supplies the `actual` (see clr/Greeter.kt); on DotKt the expect/actual collapses to the
// actual at emit, so the consumer re-imports one ordinary `Greeter` class. Consuming it cross-module (Greeter().say()
// == "Hello from the CLR actual") proves the common->platform module split + expect/actual resolution ran through
// MSBuild AND that the resulting type round-trips.
package mpp.app

expect class Greeter {
    fun say(): String
}
