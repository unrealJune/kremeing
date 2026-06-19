package com.kremeing.auto.logic

/**
 * Detects stores that have *just* turned their hot light on between two
 * snapshots of the nearby list. The car screen polls the backend periodically;
 * comparing successive snapshots lets it highlight (or locally alert about)
 * stores that flipped on, independently of the server-side push fan-out.
 *
 * Stateless: callers hold the previous snapshot and pass both in.
 */
object FlipDetector {

    /**
     * Stores that are lit in [current] but were not lit in [previous] (either
     * absent before, or present but off/unknown). Ordered by ascending
     * distance, like the screen list itself.
     */
    fun newlyLit(
        previous: List<NearbyStore>,
        current: List<NearbyStore>,
    ): List<NearbyStore> {
        val previouslyLit: Set<Int> =
            previous.asSequence().filter { it.isLit }.map { it.id }.toSet()
        return current
            .asSequence()
            .filter { it.isLit && it.id !in previouslyLit }
            .sortedWith(compareBy({ it.distanceMiles }, { it.id }))
            .toList()
    }

    /** True when at least one store flipped on between the two snapshots. */
    fun hasNewlyLit(
        previous: List<NearbyStore>,
        current: List<NearbyStore>,
    ): Boolean = newlyLit(previous, current).isNotEmpty()
}
