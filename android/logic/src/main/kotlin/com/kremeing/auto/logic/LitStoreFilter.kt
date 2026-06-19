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

    /**
     * Lit stores only, sorted by ascending [NearbyStore.distanceMiles], with
     * ties broken by store id for a stable order, limited to [limit] rows.
     *
     * @param stores the raw nearby-stores list from the backend
     * @param limit  maximum rows to return (defaults to [MAX_ROWS]); values
     *               <= 0 yield an empty list
     */
    fun litNearby(stores: List<NearbyStore>, limit: Int = MAX_ROWS): List<NearbyStore> {
        if (limit <= 0) return emptyList()
        return stores
            .asSequence()
            .filter { it.isLit }
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
