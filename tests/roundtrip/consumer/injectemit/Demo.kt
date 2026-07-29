// ktproj-injectemit (#15 EMIT-HALF) CONSUMER-LOCAL half: a LOCAL copy of the `demo` package, pulled into the
// consumer's OWN compilation by its recursive `**/*.kt` glob. The consumer ALSO <ProjectReference>s the producer,
// whose RoundtripProducer.dll EXPORTS the same `demo.Plain`/`demo.hello` identities (see ../producer/Demo.kt). So
// `demo.Plain`/`demo.hello` are compiled LOCALLY here AND available from the producer's reference KLIB — the exact
// #15 pathological overlap. Frontend source precedence suppresses the external copy; bir2cir must prefer this local
// BIR type over the referenced dll of the same FQN (a local `new demo.Plain`, NOT a `newClr` against the ref, which
// would have the consumer both emit `demo.Plain` locally AND newClr the ref copy -> ilemit "type already defined").
// This is the ONE deliberate exception to the consumer's DLL-not-source invariant — it exists precisely to force the
// local-vs-ref same-FQN collision the #15 regression guards. (See KtprojTests.injectemitLocalOverRef.)
package demo

class Plain { val tag: String = "plain" }

fun hello(): Int = 42
