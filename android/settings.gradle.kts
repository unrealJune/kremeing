pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "kremeing-android"

// The pure-Kotlin/JVM logic module holds all of the app's decision logic
// (lit-store filtering, card formatting, navigation URIs, FCM parsing) and
// is always built — it has no Android dependencies, so it compiles and tests
// on any JDK + Gradle (including CI without an Android SDK or Google Maven
// access). This is where the exhaustive unit tests live.
include(":logic")

// The Android Auto app shell needs the Android Gradle Plugin, androidx.car.app
// and Firebase — all served from Google's Maven, which isn't reachable in every
// environment (e.g. restricted CI). Including it is therefore opt-in: set
// -PbuildAndroidApp=true (or env KREMEING_BUILD_ANDROID_APP=1) when building in
// an environment with an Android SDK and Google Maven access. By default only
// :logic is configured, so `./gradlew :logic:test` works everywhere.
val buildAndroidApp =
    (providers.gradleProperty("buildAndroidApp").orNull?.toBoolean() ?: false) ||
    System.getenv("KREMEING_BUILD_ANDROID_APP") != null

if (buildAndroidApp) {
    include(":app")
} else {
    gradle.rootProject {
        logger.lifecycle(
            "[kremeing-android] Building :logic only. To also build the Android Auto " +
            "app, run with -PbuildAndroidApp=true (requires Android SDK + Google Maven)."
        )
    }
}
