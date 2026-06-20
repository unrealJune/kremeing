# Keep kotlinx.serialization generated serializers for the wire models.
-keepclassmembers class com.kremeing.auto.logic.** {
    *** Companion;
}
-keepclasseswithmembers class com.kremeing.auto.logic.** {
    kotlinx.serialization.KSerializer serializer(...);
}
