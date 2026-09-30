plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("com.google.devtools.ksp")
}

// The RouteSync suite's version, from the nearest git tag, read the same way the .NET apps
// read it (see Directory.Build.props at the repository root). A commit tagged v1.2.0 is
// release 1.2.0. A commit after it is a preview of the next minor release, 1.3.0, named with
// the commit it was built from. Android orders installs by the version code, major, minor
// and patch in two digits each, so 1.2.0 is 10200 and a phone accepts each release as an
// update. With no tag to read, the build is 0.0.0.
data class SuiteVersion(val major: Int, val minor: Int, val patch: Int, val preview: Boolean, val commit: String) {
    val code get() = major * 10000 + minor * 100 + patch
    val name get() = "$major.$minor.$patch" + if (preview) "-preview+$commit" else ""
    val label get() = "$major.$minor.$patch" + if (preview) " preview · $commit" else ""
}

val suiteVersion: SuiteVersion = run {
    val described = providers.exec {
        commandLine("git", "describe", "--tags", "--match", "v[0-9]*", "--long", "--abbrev=7")
        isIgnoreExitValue = true
    }.standardOutput.asText.get().trim()
    val m = Regex("""^v(\d+)\.(\d+)\.(\d+)-(\d+)-g([0-9a-f]+)$""").find(described)
    if (m == null) SuiteVersion(0, 0, 0, true, "unknown")
    else {
        val (major, minor, patch, height, commit) = m.destructured
        if (height == "0") SuiteVersion(major.toInt(), minor.toInt(), patch.toInt(), false, commit)
        else SuiteVersion(major.toInt(), minor.toInt() + 1, 0, true, commit)
    }
}

android {
    namespace = "com.routesync.cameracount"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.routesync.cameracount"
        minSdk = 26
        targetSdk = 35
        versionCode = suiteVersion.code
        versionName = suiteVersion.name
        buildConfigField("String", "SUITE_VERSION", "\"${suiteVersion.label}\"")
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    buildFeatures {
        compose = true
        buildConfig = true
    }
    androidResources {
        noCompress += "tflite"
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.7")
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.7")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.7")
    implementation(platform("androidx.compose:compose-bom:2024.12.01"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.datastore:datastore-preferences:1.1.1")

    // On-device queue of detected crossings. DataStore holds the count, which is one
    // integer rewritten in place; crossings are thousands of rows that have to be
    // inserted, read oldest first and deleted in batches, which is a database.
    val room = "2.6.1"
    implementation("androidx.room:room-runtime:$room")
    implementation("androidx.room:room-ktx:$room")
    ksp("androidx.room:room-compiler:$room")

    // REST to Supabase (plain PostgREST, no SDK needed)
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")
    implementation("org.json:json:20240303")

    // Phase 3: camera pipeline + on-device YOLO11n inference
    val camerax = "1.4.1"
    implementation("androidx.camera:camera-core:$camerax")
    implementation("androidx.camera:camera-camera2:$camerax")
    implementation("androidx.camera:camera-lifecycle:$camerax")
    implementation("androidx.camera:camera-view:$camerax")
    implementation("org.tensorflow:tensorflow-lite:2.16.1")
    implementation("org.tensorflow:tensorflow-lite-gpu:2.16.1")
    implementation("org.tensorflow:tensorflow-lite-gpu-api:2.16.1")

    debugImplementation("androidx.compose.ui:ui-tooling")
}
