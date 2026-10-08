package com.kremeing.auto.logic

/**
 * Expands the Leaflet-style tile URL template served by `/map-config`
 * (`…/light_all/{z}/{x}/{y}{r}.png?key=…`) for one tile. `{r}` is the retina
 * suffix; the app requests 256 px tiles, so it is always empty.
 */
object BasemapTemplate {

    /** Compiled-in default, used when neither the API nor the cache has a template. */
    fun withKey(baseUrl: String, key: String): String =
        if (key.isBlank()) baseUrl else "$baseUrl?key=$key"

    fun expand(template: String, zoom: Int, x: Int, y: Int): String =
        template
            .replace("{z}", zoom.toString())
            .replace("{x}", x.toString())
            .replace("{y}", y.toString())
            .replace("{r}", "")
}
