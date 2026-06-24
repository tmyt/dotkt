// Two `Args` types in different namespaces are both injected. The override target's param type must resolve to the
// EXACT one (Aaa.Args), not whichever same-simple-name type won the dedup — so the override matches.
import Aaa.Args
import App.Base
class My : Base() {
    override fun handle(x: Args): Int = 42   // must override Base.handle(Aaa.Args)
}
fun main() { println(My().handle(Args())) }   // 42
