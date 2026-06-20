plugins {
    // Versions are declared here (not inherited from the root build) so that the
    // Kotlin Gradle plugin is loaded on this module's own classpath. See the
    // root build.gradle.kts for why plugins are declared per-module.
    kotlin("jvm") version "1.9.24"
    kotlin("plugin.serialization") version "1.9.24"
}

dependencies {
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.6.3")

    testImplementation("org.junit.jupiter:junit-jupiter:5.10.2")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}

tasks.test {
    useJUnitPlatform()
}
