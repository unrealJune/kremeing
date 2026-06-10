namespace Kremeing.Api

open System
open System.Threading

/// Thread-safe holder for the discovered store registry.
///
/// Why this exists: discovery used to resolve the store list exactly once
/// at startup and hand the immutable `RegistryEntry list` to both the
/// poller and the read handlers by value. When a startup discovery returned
/// 0 stores (an upstream blip during a pod restart), that empty list was
/// cached forever — a silent, self-perpetuating outage. The holder lets a
/// periodic refresh atomically swap the list so every reader (poller tick
/// and handler `lookupQuery`) immediately sees the latest snapshot.
module Registry =

    /// Immutable snapshot swapped atomically. Bundling the entries with the
    /// refresh timestamp means a single reference write publishes both
    /// consistently — readers never see a count from one refresh paired with
    /// a timestamp from another.
    type private Snapshot = {
        Entries: Discovery.RegistryEntry list
        RefreshedAt: DateTimeOffset
    }

    /// Single source of truth for the current registry. Reads are lock-free
    /// (a volatile read of an immutable reference); writes publish a brand
    /// new snapshot. n ≈ 400, so copying the reference — not the list — is
    /// all that happens on a swap.
    type Holder(initial: Discovery.RegistryEntry list, refreshedAt: DateTimeOffset) =
        let mutable snapshot = { Entries = initial; RefreshedAt = refreshedAt }

        /// Latest registry snapshot. Safe to call from any thread on every
        /// poller tick / handler request.
        member _.Get() : Discovery.RegistryEntry list =
            (Volatile.Read(&snapshot)).Entries

        /// Number of stores currently registered. Exposed on /health so a
        /// stuck-at-zero registry is observable instead of silently green.
        member _.Count : int =
            (Volatile.Read(&snapshot)).Entries.Length

        /// When the registry was last (re)published — startup time until the
        /// first successful refresh swaps it.
        member _.LastRefreshedAt : DateTimeOffset =
            (Volatile.Read(&snapshot)).RefreshedAt

        /// Atomically replace the registry. Only the refresh service calls
        /// this, and only with a non-empty result (see DiscoveryRefresh).
        member _.Set(entries: Discovery.RegistryEntry list, at: DateTimeOffset) : unit =
            Volatile.Write(&snapshot, { Entries = entries; RefreshedAt = at })
