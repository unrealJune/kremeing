// Root build file. No Gradle plugins are declared (or put on a shared classpath)
// here on purpose. Each module declares the plugins it needs — with their
// version — in its own build script:
//
//   * :logic declares the JVM Kotlin plugins (resolved from Maven Central).
//   * :app declares the Android Gradle Plugin together with the Kotlin-Android
//     plugin (resolved from Google's Maven, opt-in via -PbuildAndroidApp).
//
// Declaring them per-module — rather than once here with `apply false` — keeps
// the Kotlin Gradle plugin and the Android Gradle Plugin on the SAME module
// classpath for :app. Applying `org.jetbrains.kotlin.android` instantiates
// KotlinAndroidTarget, which references AGP classes such as
// `com.android.build.gradle.api.BaseVariant`; those are only visible when the
// Kotlin plugin and AGP are loaded together on :app's own classpath. If the
// Kotlin plugin were placed on the root classpath (without AGP), applying it in
// :app would fail with "Could not generate a decorated class for type
// KotlinAndroidTarget > com/android/build/gradle/api/BaseVariant".
//
// Keeping AGP out of any shared root classpath also preserves the default
// `:logic`-only build's independence from Google Maven (see settings.gradle.kts
// for the opt-in gating).
