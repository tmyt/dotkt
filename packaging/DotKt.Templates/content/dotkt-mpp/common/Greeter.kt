package dotktapp

// Common (platform-agnostic) fragment: sources under common/ carry the `expect` declarations.
// The CLR `actual` lives in clr/Greeter.kt.
expect class Greeter() {
    fun greeting(who: String): String
}
