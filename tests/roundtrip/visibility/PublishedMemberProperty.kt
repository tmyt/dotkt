import roundtrip.publishedcallables.Holder

fun read(value: Holder): Int { value.member = 1; return value.member }
