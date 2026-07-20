// ktproj-injectemit (#15 EMIT-HALF) PRODUCER half: this producer emits `demo.Plain`/`demo.hello` into
// RoundtripProducer.dll. The consumer ALSO compiles a LOCAL copy of this exact package (consumer/injectemit/Demo.kt,
// pulled in by its recursive `**/*.kt` glob) WHILE <ProjectReference>ing this producer — so `demo.Plain`/`demo.hello`
// are BOTH compiled locally by the consumer AND exported by the referenced dll. The frontend "source wins" fix
// suppresses the injected copy; bir2cir must then PREFER the local BIR type over the referenced dll of the same FQN
// (a local `new demo.Plain`, NOT a `newClr` against this dll — which would make the consumer both emit `demo.Plain`
// locally AND newClr the ref copy -> ilemit conflict). Regression guard for the local-over-ref resolution.
package demo

class Plain { val tag: String = "plain" }

fun hello(): Int = 42
