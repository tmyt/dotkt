// #351 — dll2klib must publish a CLR class type parameter's declared bound. If the bound is absent from the
// reference KLIB, the frontend accepts this invalid instantiation and leaves the CLR to reject it later.
import ClassConstraintInterop.Box
import ClassConstraintInterop.NotSink

fun main() {
    Box<NotSink>()
}
