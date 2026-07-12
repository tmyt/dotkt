package mpp.app

// COMMON (platform-agnostic) fragment: an `expect` declaration with NO body. Files under common/ are
// tagged as the common source set by the MPP targets (-Xcommon-sources), gated on <DotKtMultiplatform>.
// The sole platform — CLR — supplies the `actual` (see clr/Greeter.kt).
expect class Greeter {
    fun say(): String
}
