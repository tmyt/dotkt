// N6 — INTERFACE .NET events subscribe with the idiomatic `+=` / `-=` on an interface-typed receiver.
// facadegen now surfaces a public INSTANCE event of an INTERFACE (INotifyPropertyChanged.PropertyChanged) as a
// `kotlin.clr.ClrEvent<T>` abstract member; bir2cir's ClrEventOperatorBinding binds the operator to the interface
// event's add/remove accessor (a callvirt on the interface slot). ObservableCollection<T> implements
// INotifyPropertyChanged EXPLICITLY, so the interface-typed view exposes PropertyChanged; Add() raises it.
import System.Collections.ObjectModel.ObservableCollection
import System.ComponentModel.INotifyPropertyChanged

fun main() {
    val c = ObservableCollection<Int>()
    val n: INotifyPropertyChanged = c        // interface-typed receiver (explicit interface implementation)
    var fired = 0
    val h: (Any?, Any?) -> Unit = { _, _ -> fired++ }
    n.PropertyChanged += h                    // subscribe on the INTERFACE-typed receiver
    c.Add(10)                                 // raises PropertyChanged -> handler fires
    c.Add(20)
    n.PropertyChanged -= h                     // unsubscribe (delegate equality)
    c.Add(30)                                 // handler no longer fires
    println("count=${c.Count}")               // count=3
    println("fired=${fired > 0}")             // fired=true
}
