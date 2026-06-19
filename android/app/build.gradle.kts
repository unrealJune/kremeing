plugins {
    id("com.android.application") version "8.6.1"
    // The Kotlin Gradle plugin (shared by kotlin-android and kotlin-jvm) is
    // already on the build classpath because the root build declares
    // `kotlin("jvm") version "1.9.24"`. Re-declaring a version here fails with
    // "the plugin is already on the classpath with an unknown version", so the
    // Kotlin plugins are applied without a version and inherit the root's.
    id("org.jetbrains.kotlin.android")
    kotlin("plugin.serialization")
}

android {
    namespace = "com.kremeing.auto"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.kremeing.auto"
        minSdk = 29
        targetSdk = 34
        versionCode = 1
        versionName = "1.0"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"

        // Base URL of the Kremeing backend the app polls and subscribes against.
        // Overridable per build without touching code.
        buildConfigField(
            "String",
            "KREMEING_BASE_URL",
            "\"${project.findProperty("kremeingBaseUrl") ?: "https://kremeing.example.com"}\"",
        )
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
        }
    }

    buildFeatures {
        buildConfig = true
    }

    // Layer 1 (Robolectric) runs the Android-framework UX tests on the JVM with
    // no emulator. includeAndroidResources lets Robolectric read resources
    // (e.g. notification channel strings) the way the device would.
    testOptions {
        unitTests {
            isIncludeAndroidResources = true
            isReturnDefaultValues = true
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    implementation(project(":logic"))

    // Android Auto / Car App Library — provides the templated POI card UX.
    implementation("androidx.car.app:app:1.4.0")
    implementation("androidx.car.app:app-automotive:1.4.0")

    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")

    // Push transport. Added as a plain dependency (no google-services plugin),
    // so the module compiles without a google-services.json; that file is only
    // needed at deploy time to initialize Firebase on a real device.
    implementation(platform("com.google.firebase:firebase-bom:33.1.2"))
    implementation("com.google.firebase:firebase-messaging-ktx")

    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.6.3")

    // Layer 1 — Robolectric JVM tests of the Android Auto UX, notifications and
    // the companion activity. androidx.car.app:app-testing provides the
    // ScreenController / TestCarContext used to drive the car screen.
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.robolectric:robolectric:4.13")
    testImplementation("androidx.test:core:1.6.1")
    testImplementation("androidx.test.ext:junit:1.2.1")
    testImplementation("androidx.car.app:app-testing:1.4.0")

    // Layer 2 — thin on-emulator Espresso smoke test (opt-in CI job).
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test:rules:1.6.1")
    androidTestImplementation("androidx.test.espresso:espresso-core:3.6.1")
}
