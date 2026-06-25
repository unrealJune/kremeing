plugins {
    // AGP and the Kotlin plugins are declared together here, with versions, so
    // they load on THIS module's classpath (applying kotlin.android needs AGP's
    // classes on the same classpath — see android/build.gradle.kts).
    //
    // Modernized toolchain: AGP 8.9.1 + Kotlin 2.2.21 + compileSdk 36, required
    // to consume de.afarber:openmapview (built with Kotlin 2.2 metadata).
    id("com.android.application") version "8.9.1"
    id("org.jetbrains.kotlin.android") version "2.2.21"
    kotlin("plugin.serialization") version "2.2.21"
}

android {
    namespace = "com.kremeing.auto"
    compileSdk = 36

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

        // Whether the app wires up FCM push + notifications. Turn this off
        // (-PkremeingPushEnabled=false) to build a "notificationless" APK that
        // skips the token/subscribe flow entirely — useful for CI-built debug
        // APKs that ship without a google-services.json, where fetching an FCM
        // token would otherwise fail with "Couldn't get a push token".
        buildConfigField(
            "boolean",
            "PUSH_ENABLED",
            "${project.findProperty("kremeingPushEnabled") ?: "true"}",
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
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

dependencies {
    implementation(project(":logic"))
    // Android Auto / Car App Library — provides the templated car UX. 1.7.0 adds
    // the WEATHER category + MapWithContentTemplate (car API 7), letting the app
    // draw its own canvas/surface under MAP_TEMPLATES without being a nav app.
    implementation("androidx.car.app:app:1.7.0")
    implementation("androidx.car.app:app-automotive:1.7.0")

    // Live location for the car card (FusedLocationProviderClient): the screen
    // queries the driver's current position each refresh so the nearby list
    // tracks them while driving.
    implementation("com.google.android.gms:play-services-location:21.3.0")

    // Native map for the phone home screen — osmdroid (OpenStreetMap), smooth
    // multi-touch zoom + disk tile caching, no API key.
    implementation("org.osmdroid:osmdroid-android:6.1.20")

    // Material components: bottom sheet for the store-detail / heat-bar panel.
    implementation("com.google.android.material:material:1.12.0")

    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")

    // Push transport. Added as a plain dependency (no google-services plugin),
    // so the module compiles without a google-services.json; that file is only
    // needed at deploy time to initialize Firebase on a real device.
    implementation(platform("com.google.firebase:firebase-bom:33.1.2"))
    implementation("com.google.firebase:firebase-messaging-ktx")

    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.7.3")

    // Layer 1 — Robolectric JVM tests of the Android Auto UX, notifications and
    // the companion activity. androidx.car.app:app-testing provides the
    // ScreenController / TestCarContext used to drive the car screen.
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.robolectric:robolectric:4.13")
    testImplementation("androidx.test:core:1.6.1")
    testImplementation("androidx.test.ext:junit:1.2.1")
    testImplementation("androidx.car.app:app-testing:1.7.0")

    // Layer 2 — thin on-emulator Espresso smoke test (opt-in CI job).
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test:rules:1.6.1")
    androidTestImplementation("androidx.test.espresso:espresso-core:3.6.1")
}
