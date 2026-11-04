# Consumer rules for minified (R8/ProGuard) player builds: the managed bridge calls these classes
# through JNI by name, which shrinkers cannot see.
-keep class com.turbohttp.background.** { *; }
