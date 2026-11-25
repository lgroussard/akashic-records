plugins {
    id("com.android.application") version "8.5.2"
    id("org.jetbrains.kotlin.android") version "1.9.24"
    id("com.google.devtools.ksp") version "1.9.24-1.0.20"
}

android {
    namespace = "com.akashicrecords"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.akashicrecords"
        minSdk = 24
        targetSdk = 34

        // yt-dlp ships native Python per ABI: phones (arm64) + emulator (x86_64) only, to keep the APK small.
        ndk { abiFilters += listOf("arm64-v8a", "x86_64") }
        versionCode = 1
        versionName = "1.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            // Signed with the same (debug) key as every APK installed so far, so the release build updates
            // the installed app in place and keeps its data. Release is what goes on the phone: a debug
            // build is debuggable, which makes Compose screens several times slower to open.
            signingConfig = signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        isCoreLibraryDesugaringEnabled = true
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    // youtubedl-android runs its bundled Python from extracted .so files.
    packaging {
        jniLibs { useLegacyPackaging = true }
    }
    buildFeatures {
        compose = true
    }
    composeOptions {
        kotlinCompilerExtensionVersion = "1.5.14"
    }
}

ksp {
    arg("room.schemaLocation", "$projectDir/schemas")
}

dependencies {
    coreLibraryDesugaring("com.android.tools:desugar_jdk_libs:2.0.4")
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.activity:activity-compose:1.9.2")
    implementation(platform("androidx.compose:compose-bom:2024.09.02"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    // Calendar reminders (periodic check, survives reboots).
    implementation("androidx.work:work-runtime-ktx:2.9.1")
    // Home-screen widgets (agenda, journal, music).
    implementation("androidx.glance:glance-appwidget:1.1.0")
    implementation("androidx.compose.material:material-icons-core")
    implementation("androidx.media3:media3-exoplayer:1.4.1")
    implementation("androidx.media3:media3-session:1.4.1")
    // yt-dlp + Python + ffmpeg bundled for on-phone downloads (same tool as the desktop downloader).
    implementation("io.github.junkfood02.youtubedl-android:library:0.17.2")
    implementation("io.github.junkfood02.youtubedl-android:ffmpeg:0.17.2")
    implementation("androidx.room:room-runtime:2.6.1")
    implementation("androidx.room:room-ktx:2.6.1")
    ksp("androidx.room:room-compiler:2.6.1")
    testImplementation("junit:junit:4.13.2")
    // The real org.json for JVM tests (the Android jar only has stubs of it).
    testImplementation("org.json:json:20240303")
}
