package mpp.app

import roundtrip.dispatchsurface.NamedCompanionHost
import roundtrip.ownership.ShadowOwner

// Assembly B publishes Assembly A's singleton companion in its own signature. dll2klib sees a TypeRef here, not the
// carrier TypeDef it already maps while projecting A, and must restore the same semantic nested classifier.
fun passNamedCompanion(value: NamedCompanionHost.Key): NamedCompanionHost.Key = value

// Assembly B re-exports assembly A's generic inner type. The physical signature contains the outer capture prefix;
// dll2klib must recover Kotlin's own-only classifier arguments when assembly C consumes B.
fun passShadowedInner(value: ShadowOwner<Int>.Entry<String>): ShadowOwner<Int>.Entry<String> = value
