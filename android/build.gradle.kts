// TC-tools Unlock — Android root build script
// Toolchain baseline (must stay mutually compatible):
//   AGP 8.5.2 + Kotlin 2.0.21 + Compose BOM 2024.09.00 + JDK 17
plugins {
    id("com.android.application") version "8.5.2" apply false
    id("org.jetbrains.kotlin.android") version "2.0.21" apply false
    id("org.jetbrains.kotlin.plugin.compose") version "2.0.21" apply false
}

tasks.register<Delete>("clean") {
    delete(rootProject.layout.buildDirectory)
}
