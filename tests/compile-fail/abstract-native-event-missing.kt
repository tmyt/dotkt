import System.ComponentModel.INotifyPropertyChanged
import System.ComponentModel.PropertyChangedEventHandler
import kotlin.clr.ClrEvent
import kotlin.clr.clrEvent

abstract class DeferredNativeAccessors : INotifyPropertyChanged
class MissingDeferredNativeAccessors : DeferredNativeAccessors()

enum class MissingEnumNativeAccessors : INotifyPropertyChanged {
    Present { override val PropertyChanged: ClrEvent<PropertyChangedEventHandler> by clrEvent() },
    Missing
}
