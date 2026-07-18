package dotktapp

// CLR platform fragment: everything outside common/ is the `actual` (platform) source set.
actual class Greeter {
    actual fun greeting(who: String): String = "Hello, $who, from a DotKt multiplatform app on .NET!"
}
