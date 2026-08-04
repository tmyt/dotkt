package mpp.app

import roundtrip.dispatchsurface.NamedCompanionHost

// Assembly B publishes Assembly A's singleton companion in its own signature. dll2klib sees a TypeRef here, not the
// carrier TypeDef it already maps while projecting A, and must restore the same semantic nested classifier.
fun passNamedCompanion(value: NamedCompanionHost.Key): NamedCompanionHost.Key = value
