// Root build file. Only the JVM Kotlin plugins (resolved from Maven Central)
// are declared here for the always-built `:logic` module. The Android Gradle
// Plugin and Kotlin-Android plugin are declared inside `:app` instead, so a
// default build that doesn't include `:app` never needs to reach Google's
// Maven to resolve them (see settings.gradle.kts for the opt-in gating).
plugins {
    kotlin("jvm") version "1.9.24" apply false
    kotlin("plugin.serialization") version "1.9.24" apply false
}
