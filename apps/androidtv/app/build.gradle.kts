import javax.inject.Inject

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

// The app's id lives here (and its name in strings.xml), so both change in one place.
val appId = "com.ovmplayer.tv"

android {
    namespace = appId
    compileSdk = 37

    defaultConfig {
        applicationId = appId
        minSdk = 26
        // -PtargetSdkOverride=37 builds a test variant for checking Android 17 behaviour (local-network permission).
        targetSdk = (project.findProperty("targetSdkOverride") as String?)?.toInt() ?: 36
        versionCode = 1
        versionName = "0.1.0"
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

    buildFeatures {
        compose = true
        buildConfig = true
    }
}

// The menu renderer is the same TypeScript bundle the browser preview uses; copy it in at build time.
abstract class CopyRendererTask @Inject constructor(private val fs: FileSystemOperations) : DefaultTask() {
    @get:InputFiles
    abstract val bundle: ConfigurableFileCollection

    @get:OutputDirectory
    abstract val outputDir: DirectoryProperty

    @TaskAction
    fun copy() {
        check(bundle.files.all { it.exists() }) {
            "Renderer bundle missing: run `npm run build` in the repository root first."
        }
        fs.sync {
            from(bundle)
            into(outputDir.dir("menu"))
        }
    }
}

val rendererDist = rootProject.file("../../packages/renderer-web/dist")
val copyRenderer = tasks.register<CopyRendererTask>("copyRenderer") {
    bundle.from(rendererDist.resolve("openvideomenu-player.js"), rendererDist.resolve("host.html"))
    outputDir.set(layout.buildDirectory.dir("generated/renderer-assets"))
}
androidComponents {
    onVariants { variant ->
        variant.sources.assets?.addGeneratedSourceDirectory(copyRenderer, CopyRendererTask::outputDir)
    }
}

dependencies {
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.foundation)
    implementation(libs.compose.ui.tooling.preview)
    implementation(libs.tv.material)
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.webkit)
    implementation(libs.media3.exoplayer)
    implementation(libs.media3.ui)
    implementation(libs.media3.ffmpeg)
    implementation(libs.okhttp)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.coil.compose)
    implementation(libs.coil.okhttp)
    testImplementation(libs.junit)
}
