plugins {
	kotlin("jvm") version "2.2.0"
	application
}

repositories {
	mavenCentral()
}

dependencies {
	// The whole point: reuse the official frontend (Configuration / FIR / Fir2Ir).
	// We only own the backend (Kotlin IR -> C#) on top of this.
	implementation("org.jetbrains.kotlin:kotlin-compiler-embeddable:2.2.0")

	testImplementation(kotlin("test"))
}

kotlin {
	// Foojay auto-downloads a matching JDK (host has only a JRE). This also drives javac.
	jvmToolchain(21)
}

application {
	// CLI entry point of the Kotlin/CLR compiler (.kt -> BIR). The launcher binary, the module, and this dir are all
	// `kotc`; the internal Kotlin package is `kotc.*` too.
	mainClass.set("kotc.MainKt")
}

tasks.test {
	useJUnitPlatform()
}
