# R8 rules for the release build (see build.gradle.kts).

# Optimise and remove unused code, but keep every name: a crash report then reads like the source.
-dontobfuscate
-keepattributes SourceFile,LineNumberTable,*Annotation*,Signature,InnerClasses,EnclosingMethod

# yt-dlp on the phone (youtubedl-android): it maps yt-dlp's JSON onto its classes with Jackson, by reflection,
# and unpacks Python with commons-compress. Kept whole rather than guessing which members are reached.
-keep class com.yausername.** { *; }
-keep class com.fasterxml.jackson.** { *; }
-keep class org.apache.commons.compress.** { *; }
-dontwarn com.fasterxml.jackson.**
-dontwarn org.apache.commons.compress.**
-dontwarn org.brotli.dec.**
-dontwarn org.tukaani.xz.**
-dontwarn com.github.luben.zstd.**
-dontwarn org.objectweb.asm.**
-dontwarn java.beans.**
-dontwarn javax.xml.**

# Home-screen widget buttons: Glance creates them from their class name when tapped.
-keep class * implements androidx.glance.appwidget.action.ActionCallback { <init>(); }
