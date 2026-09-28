package roundtrip.storedsam

import System.Threading.ThreadStart

inline fun inlineThreadStart(noinline callback: () -> Unit): ThreadStart = ThreadStart(callback)
fun defaultThreadStart(callback: () -> Unit, converted: ThreadStart = ThreadStart(callback)): ThreadStart = converted
fun returnedCallback(callback: () -> Unit): () -> Unit = callback
