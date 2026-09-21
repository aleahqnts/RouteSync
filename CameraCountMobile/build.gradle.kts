plugins {
    id("com.android.application") version "8.7.3" apply false
    id("org.jetbrains.kotlin.android") version "2.0.21" apply false
    id("org.jetbrains.kotlin.plugin.compose") version "2.0.21" apply false
    // Room's annotation processor. The version's prefix has to be the Kotlin version
    // above: KSP is built against one compiler and refuses to run under another.
    id("com.google.devtools.ksp") version "2.0.21-1.0.28" apply false
}
