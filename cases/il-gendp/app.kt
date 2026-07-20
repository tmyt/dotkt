// #191: a GENERIC user delegate `D<T>` backing a delegated property. kotc names the delegate owner with the BARE
// FQN (`"D"`, no type args) on the getValue/setValue dispatch while the `$delegate` field/local carries the
// constructed `D<String>`/`D<Int>` — the open owner mismatched the constructed receiver (BadImageFormatException /
// ilverify `found 'string' expected '!0'`). bir2cir GenericDelegateInstantiation recovers the receiver's
// instantiation and stamps the constructed owner. Covers MEMBER + LOCAL + TOP-LEVEL delegated properties over both
// a REFERENCE (String) and a VALUE (Int) type arg — the full generic `viewModelProperty<T>` MVVM property side.
import kotlin.reflect.KProperty

class D<T>(private var v: T) {
    operator fun getValue(r: Any?, p: KProperty<*>): T = v
    operator fun setValue(r: Any?, p: KProperty<*>, nv: T) { v = nv }
}

// generic extension-fun delegate provider (the viewModelProperty<T> shape)
open class ViewModelBase { var raised = 0 }
fun <T> ViewModelBase.viewModelProperty(initial: T) = D(initial)

class PersonViewModel : ViewModelBase() {
    var name: String by viewModelProperty("John")   // member, reference type arg
    var age: Int by D(30)                            // member, value type arg
}

var topName: String by D("top")                       // top-level, reference type arg

// generic ENCLOSING class: the `$delegate` field owner is the constructed `Container<String>`, and getValue/
// setValue dispatch on the constructed `D<String>` — both instantiations must be carried (bir2cir #191 part b).
class Container<T>(init: T) { var item: T by D(init) }

fun main() {
    val pvm = PersonViewModel()
    println(pvm.name)          // John
    pvm.name = "Jane"
    println(pvm.name)          // Jane
    println(pvm.age)           // 30
    pvm.age = 42
    println(pvm.age)           // 42

    println(topName)           // top
    topName = "changed"
    println(topName)           // changed

    var loc: Int by D(7)        // local, value type arg
    println(loc)               // 7
    loc = 99
    println(loc)               // 99

    val box = Container<String>("boxed")   // generic enclosing class, cross-scope access
    println(box.item)          // boxed
    box.item = "rebox"
    println(box.item)          // rebox
}
