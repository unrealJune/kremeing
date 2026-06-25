package com.kremeing.auto.logic

/**
 * Selects and orders the stores the Android Auto screen should show. The car
 * app only ever lists stores whose hot light is currently on, nearest first,
 * optionally capped so the driver isn't shown an unbounded list while moving.
 *
 * Pure and deterministic — this is the heart of "what appears on the screen",
 * so it's exhaustively unit-tested rather than exercised through the car UI.
 */
object LitStoreFilter {

    /** Hard ceiling on rows; Android Auto templates cap list length anyway. */
    const val MAX_ROWS: Int = 6

    /** Row ceiling for the full nearby list (lit + off); the host caps too. */
    const val MAX_ROWS_ALL: Int = 12

    /**
     * All nearby stores — lit *and* off — ranked for the car browse list:
     * every store within [radiusMiles] comes first (even if its light is off),
     * then all stores outside the radius. Within each group, lit stores lead,
     * then nearest first (ties broken by id).
     *
     * The radius reflects the user's notification area, so "my stores" stay
     * pinned to the top while more distant stores remain browsable below.
     *
     * @param stores      the raw nearby-stores list from the backend
     * @param radiusMiles the notification radius; stores at or within it rank first
     * @param limit       maximum rows (defaults to [MAX_ROWS_ALL]); <= 0 yields empty
     */
    fun nearbyRanked(
        stores: List<NearbyStore>,
        radiusMiles: Double,
        limit: Int = MAX_ROWS_ALL,
    ): List<NearbyStore> {
        if (limit <= 0) return emptyList()
        return stores
            .asSequence()
            .sortedWith(
                compareBy(
                    { it.distanceMiles > radiusMiles },  // within-radius group first
                    { !it.isLit },                        // lit first within each group
                    { it.distanceMiles },                 // then nearest
                    { it.id },                            // stable tiebreak
                ),
            )
            .take(limit)
            .toList()
    }

    /**
     * All nearby stores — lit *and* off — ordered lit first then nearest (ties
     * broken by id), ignoring any radius. Kept for callers that want a simple
     * "what's around me" list.
     */
    fun nearbyAll(stores: List<NearbyStore>, limit: Int = MAX_ROWS_ALL): List<NearbyStore> {
        if (limit <= 0) return emptyList()
        return stores
            .asSequence()
            .sortedWith(compareBy({ !it.isLit }, { it.distanceMiles }, { it.id }))
            .take(limit)
            .toList()
    }

    /**
     * Lit stores only, optionally within [maxDistanceMiles], sorted by ascending
     * [NearbyStore.distanceMiles] with ties broken by store id for a stable
     * order, limited to [limit] rows.
     *
     * @param stores          the raw nearby-stores list from the backend
     * @param maxDistanceMiles if non-null, drops stores farther than this many
     *                         miles (inclusive at the boundary); null keeps all
     *                         distances. Non-positive values yield an empty list.
     * @param limit           maximum rows to return (defaults to [MAX_ROWS]);
     *                        values <= 0 yield an empty list
     */
    fun litNearby(
        stores: List<NearbyStore>,
        maxDistanceMiles: Double? = null,
        limit: Int = MAX_ROWS,
    ): List<NearbyStore> {
        if (limit <= 0) return emptyList()
        if (maxDistanceMiles != null && maxDistanceMiles <= 0.0) return emptyList()
        return stores
            .asSequence()
            .filter { it.isLit }
            .filter { maxDistanceMiles == null || it.distanceMiles <= maxDistanceMiles }
            .sortedWith(compareBy({ it.distanceMiles }, { it.id }))
            .take(limit)
            .toList()
    }

    /** The single nearest lit store, or null if none are lit. */
    fun nearestLit(stores: List<NearbyStore>): NearbyStore? =
        litNearby(stores, limit = 1).firstOrNull()

    /** Whether any nearby store is currently lit. */
    fun anyLit(stores: List<NearbyStore>): Boolean = stores.any { it.isLit }
}
