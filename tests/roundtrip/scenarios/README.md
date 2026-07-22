# Round-trip shell scenarios

This directory contains the remaining Kotlin-to-CLR-to-Kotlin scenarios that cannot yet be represented by
the NUnit project-reference fixture. They cover expected compile failures, direct metadata inspection, and
runtime-green assemblies with known formal-verification gaps. Ordinary round-trip behavior belongs in the
adjacent `producer` and `consumer` NUnit projects.
