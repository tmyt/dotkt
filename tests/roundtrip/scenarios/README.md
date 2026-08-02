# Round-trip shell scenarios

This directory contains the remaining Kotlin-to-CLR-to-Kotlin scenarios that cannot yet be represented by
the NUnit project-reference fixture. They cover expected compile failures, direct metadata inspection, and
runtime-green assemblies with known formal-verification gaps. Ordinary round-trip behavior belongs in the
adjacent `producer` and `consumer` NUnit projects.

The nullable-generic probes retain one process and one verdict per observable so a crashing XFAIL cannot hide a
later result, but their sources are compiled as two batches: one same-module assembly and one shared
producer/consumer pair. Do not turn a new verdict into another compiler invocation.
