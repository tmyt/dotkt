package roundtrip.nullableinvariantflow

fun invokeBoxReceiver(receiver: Receiver<String>, value: Box<String>): String = receiver.echo(value)
fun invokeValueReceiver(receiver: Receiver<String>, value: String): String = receiver.echo(value)
fun invokeAcceptReceiver(receiver: Receiver<String>, value: Box<String>) { receiver.accept(value) }
