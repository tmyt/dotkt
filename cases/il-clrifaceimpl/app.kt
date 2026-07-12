// A Kotlin class IMPLEMENTING a facadegen-injected .NET INTERFACE (not extending a base class). The existing
// interop-override samples only EXTEND a base class (kotc's own isOverride stamps override:true); the
// INTERFACE-impl path is re-stamped override:true/vis:public by bir2cir's DeclarationRename off the injected
// interface member, and its slot is filled so the CLR — and a BCL consumer — dispatch into the override.
import System.Collections.Generic.IComparer
import System.Collections.Generic.List

// Implement System.Collections.Generic.IComparer<T> (a facadegen-injected .NET generic interface). The injected
// Compare surfaces its unconstrained T params as nullable (`String?`), so the override matches that signature.
class LenCmp : IComparer<String> {
    override fun Compare(x: String?, y: String?): Int = (x ?: "").length - (y ?: "").length
}

fun main() {
    val c = LenCmp()
    println(c.Compare("ab", "z"))          // 1   direct call on the implementing class
    val i: IComparer<String> = LenCmp()    // upcast to the injected .NET interface type
    println(i.Compare("z", "abcd"))        // -3  dispatched through the interface slot

    // The BCL itself dispatches into our override: List<T>.Sort(IComparer<T>).
    val xs = List<String>()
    xs.Add("abcd"); xs.Add("z"); xs.Add("bb")
    xs.Sort(c)
    println(xs[0] + "," + xs[1] + "," + xs[2])   // z,bb,abcd
}
