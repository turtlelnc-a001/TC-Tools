import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "com.tctools.unlock"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.tctools.unlock"
        minSdk = 26
        targetSdk = 34
        versionCode = 1
        versionName = "0.1.0"
        resourceConfigurations += setOf("zh", "en")
        vectorDrawables { useSupportLibrary = true }
    }

    buildTypes {
        debug {
            isMinifyEnabled = false
        }
        release {
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
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

    packaging {
        resources.excludes += setOf(
            "/META-INF/{AL2.0,LGPL2.1}",
            "META-INF/DEPENDENCIES",
            "META-INF/LICENSE*",
            "META-INF/NOTICE*",
        )
    }

    lint {
        abortOnError = false
        checkReleaseBuilds = false
    }

    testOptions {
        unitTests.all {
            it.testLogging {
                events("passed", "failed", "skipped")
                showStandardStreams = true
                exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL
            }
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
        freeCompilerArgs.add("-Xjvm-default=all")
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("androidx.activity:activity-compose:1.9.2")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.6")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.6")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.6")

    val composeBom = platform("androidx.compose:compose-bom:2024.09.00")
    implementation(composeBom)
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-graphics")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-core")
    debugImplementation("androidx.compose.ui:ui-tooling")

    implementation("androidx.biometric:biometric:1.1.0")
    implementation("androidx.security:security-crypto:1.1.0-alpha06")
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")

    testImplementation("junit:junit:4.13.2")
}

/**
 * 回环自测（协议 §7 要求：无真机也能验证密码学与消息结构）。
 * 在桌面 JVM 上运行 main()，输出 §8 向量并写入 JSON，供验证负责人逐字节比对。
 */
afterEvaluate {
    val unitTestRuntime = configurations.getByName("debugUnitTestRuntimeClasspath")
    val kotlinClasses = layout.buildDirectory.dir("tmp/kotlin-classes/debug")
    val selfTestJson = layout.buildDirectory.file("selftest/vectors-android.json")

    tasks.register<JavaExec>("selfTest") {
        group = "verification"
        description = "运行 TC-tools Unlock 协议回环自测（JVM，无需 Android 设备）"
        dependsOn("compileDebugKotlin")
        classpath = files(kotlinClasses) + unitTestRuntime
        mainClass.set("com.tctools.unlock.protocol.SelfTestMainKt")
        args(selfTestJson.get().asFile.absolutePath)
        doFirst { selfTestJson.get().asFile.parentFile.mkdirs() }
    }
}
