import System.ComponentModel.INotifyPropertyChanged
import System.ComponentModel.PropertyChangedEventHandler
import kotlin.clr.ClrEventPolicy

class OrdinaryPolicy : ClrEventPolicy<PropertyChangedEventHandler>() {
    override fun add(handler: PropertyChangedEventHandler) {}
    override fun remove(handler: PropertyChangedEventHandler) {}
    override fun invoke(vararg args: Any?) {}
}

class MissingNativeAccessors : INotifyPropertyChanged {
    override val PropertyChanged by OrdinaryPolicy()
}
