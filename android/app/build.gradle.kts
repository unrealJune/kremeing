plugins {
    // AGP and the Kotlin plugins are declared together here, with versions, so
    // they load on THIS module's classpath. Applying org.jetbrains.kotlin.android
    // needs AGP's classes (e.g. com.android.build.gradle.api.BaseVariant) on the
    // same classpath; that only holds when AGP and Kotlin are requested together
    // in this block (the root build deliberately puts neither on a shared
    // classpath — see android/build.gradle.kts).
    //
    // AGP is pinned to 8.1.x because the Kotlin version used here (1.9.24) only
    // supports AGP up to 8.1; newer AGP makes kotlin.android fail to apply with
    // "Could not generate a decorated class for type KotlinAndroidTarget".
    id("com.android.application") version "8.1.4"
    id("org.jetbrains.kotlin.android") version "1.9.24"
    kotlin("plugin.serialization") version "1.9.24"
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
