// #37 finding 1: a PackageReference whose @(ReferenceCopyLocalPaths) carries BOTH lib/<tfm>/Foo.dll AND
// runtimes/<rid>/lib/<tfm>/Foo.dll for one identity (System.IO.Ports: a RID-impl package). ilemit's runtime
// catalog must dedup by identity and select the HOST-RID asset — on Linux the runtimes/unix/lib build is the
// real implementation and the plain lib asset is a PlatformNotSupported placeholder. Selecting the placeholder
// (keep-first) would throw at runtime; the old catalog hard-failed at emit on the duplicate simple name.
import System.IO.Ports.SerialPort

fun main() {
    // GetPortNames() on the real unix impl enumerates /dev/tty* (0 on a CI box); the PNSE placeholder throws.
    val ports = SerialPort.GetPortNames()
    println("ports " + ports.size)
}
