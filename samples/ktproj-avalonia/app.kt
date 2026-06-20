// The point of Kotlin.NET: a pure .NET binding. `Avalonia.Application` comes from a <PackageReference>
// (no facade, no UI shim) and a Kotlin class inherits it directly, overriding a virtual. Whether
// Avalonia actually renders is out of scope - this proves a PackageReference type can be a Kotlin base.
import Avalonia.Application

class MyApp : Application() {
	override fun Initialize() {
		println("MyApp.Initialize: Kotlin override of Avalonia.Application")
	}
}

fun main() {
	val app: Application = MyApp()   // a Kotlin subclass IS an Avalonia.Application
	app.Initialize()                  // virtual dispatch into the Kotlin override
	println("subclassed Avalonia.Application from Kotlin via PackageReference")
}
