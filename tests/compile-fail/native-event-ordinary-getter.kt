import System.ComponentModel.INotifyPropertyChanged
import System.ComponentModel.PropertyChangedEventHandler
import kotlin.clr.ClrEvent

interface OrdinaryEventGetter : INotifyPropertyChanged {
    val source: INotifyPropertyChanged
    override val PropertyChanged: ClrEvent<PropertyChangedEventHandler>
        get() = source.PropertyChanged
}

class OrdinaryEventConsumer(override val source: INotifyPropertyChanged) : OrdinaryEventGetter
