plugins {
    // Versions are declared here (not inherited from the root build) so that the
    // Kotlin Gradle plugin is loaded on this module's own classpath. See the
    // root build.gradle.kts for why plugins are declared per-module.
    kotlin("jvm") version "2.2.21"
    kotlin("plugin.serialization") version "2.2.21"
}

dependencies {
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.7.3")

    testImplementation("org.junit.jupiter:junit-jupiter:5.10.2")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}

tasks.test {
    useJUnitPlatform()
}

// Pin the emitted bytecode to Java 17 so the dexer in :app (AGP 8.1.4 / D8)
// can consume :logic. Without this, building on a JDK newer than 17 makes
// Kotlin emit higher class-file versions (e.g. major 65 / Java 21) that D8
// rejects with "Unsupported class file major version". Mirrors :app's
// jvmTarget = "17". Retargeting the compiler (rather than jvmToolchain(17))
// keeps the build on the JDK already running Gradle — no separate JDK 17 needed.
java {
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}
