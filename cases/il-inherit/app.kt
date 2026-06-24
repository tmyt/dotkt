// P1-1: base/interface hierarchy + protected-virtual override (façade-free injection).
import P.Base
import P.Widget
import P.Button
import P.Host

// Subclass an injected .NET class and override its PROTECTED VIRTUAL method (the WinUI App.OnLaunched pattern).
class MyApp : Base() {
    override fun Tag(): String = "derived"
}

fun main() {
    println(MyApp().Run())            // run:derived  — Base.Run() polymorphically dispatches to the override
    val host = Host()
    println(host.Show(Button()))      // show:button  — Button is assignable to the Widget parameter (supertype edge)
    val w: Widget = Button()          // upcast holds at the type level
    println(w.Name())                 // button       — virtual dispatch through the injected hierarchy
}
