package com.kremeing.auto.map

import com.kremeing.auto.logic.BasemapTemplate
import org.osmdroid.tileprovider.tilesource.OnlineTileSourceBase
import org.osmdroid.util.MapTileIndex

/**
 * osmdroid tile source for the self-hosted basemap server. `XYTileSource` can
 * only build `baseUrl + z/x/y + ext`, which has no room for the `?key=` query
 * string, so this expands the full `/map-config` template per tile instead.
 *
 * The name is deliberately not "CartoLight": osmdroid keys its on-disk cache
 * by source name, and old installs have that cache full of CARTO's
 * "API KEY REQUIRED" placeholder tiles.
 */
class KremeingBasemapTileSource(val template: String) : OnlineTileSourceBase(
    NAME,
    0,
    20,
    256,
    ".png",
    arrayOf(template),
    "© OpenStreetMap contributors © CARTO",
) {
    override fun getTileURLString(pMapTileIndex: Long): String =
        BasemapTemplate.expand(
            template,
            MapTileIndex.getZoom(pMapTileIndex),
            MapTileIndex.getX(pMapTileIndex),
            MapTileIndex.getY(pMapTileIndex),
        )

    companion object {
        const val NAME = "KremeingBasemapLight"

        /** Cache name of the retired CARTO source, purged once on upgrade. */
        const val LEGACY_NAME = "CartoLight"
    }
}
