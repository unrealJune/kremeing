plugins {
    id("com.android.application") version "8.6.1"
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

    testImplementation("org.junit.jupiter:junit-jupiter:5.10.2")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}

tasks.withType<Test> {
    useJUnitPlatform()
}
