// The nested LIBRARY source. Built into Demo.dll by the ProjectReference (Demo.ktproj, OutputType=Library),
// AND — because the app's default `**/*.kt` glob is recursive — ALSO compiled into the app itself. So the app
// declares `demo.Plain`/`demo.hello` LOCALLY *and* references a dll that exports the same identities (#15).
package demo

class Plain { val tag: String = "plain" }

fun hello(): Int = 42
