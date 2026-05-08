namespace Kremeing.Api

open System
open System.Threading.Tasks
open Npgsql
open Kremeing.Contracts.Domain
open Kremeing.Core

/// Postgres-backed Observations adapter. Same Record/History/Status
/// surface as InMemoryObservations — production composition picks
/// one or the other based on whether a connection string was supplied.
///
/// Flip-only invariant lives at the SQL layer:
///   - First Record per store inserts both store_status and flip_events
///   - Same-status Record updates only store_status.last_polled_at
///   - Different-status Record updates store_status + inserts a flip_event
/// All inside a row-locked transaction so concurrent writes can't race.
module Postgres =

    [<Literal>]
    let SchemaSql = """
        CREATE TABLE IF NOT EXISTS flip_events (
            store_id    INT NOT NULL,
            observed_at TIMESTAMPTZ NOT NULL,
            status      TEXT NOT NULL CHECK (status IN ('on','off','unknown')),
            PRIMARY KEY (store_id, observed_at)
        );

        CREATE INDEX IF NOT EXISTS flip_events_store_time
            ON flip_events (store_id, observed_at DESC);

        CREATE TABLE IF NOT EXISTS store_status (
            store_id          INT PRIMARY KEY,
            current_status    TEXT NOT NULL CHECK (current_status IN ('on','off','unknown')),
            last_polled_at    TIMESTAMPTZ NOT NULL,
            first_observed_at TIMESTAMPTZ NOT NULL,
            last_flipped_at   TIMESTAMPTZ NULL
        );

        CREATE TABLE IF NOT EXISTS push_subscriptions (
            id          BIGSERIAL PRIMARY KEY,
            store_id    INT NOT NULL,
            endpoint    TEXT NOT NULL,
            p256dh      TEXT NOT NULL,
            auth        TEXT NOT NULL,
            created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
            UNIQUE (store_id, endpoint)
        );

        CREATE INDEX IF NOT EXISTS push_subs_by_store
            ON push_subscriptions (store_id);

        CREATE INDEX IF NOT EXISTS push_subs_by_endpoint
            ON push_subscriptions (endpoint);
    """

    /// Idempotent migration. Run on startup; safe to re-run.
    let applySchema (connectionString: string) : Task =
        task {
            use conn = new NpgsqlConnection(connectionString)
            do! conn.OpenAsync()
            use cmd = new NpgsqlCommand(SchemaSql, conn)
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let private statusToWire (s: HotLightStatus) =
        match s with
        | On -> "on"
        | Off -> "off"
        | Unknown -> "unknown"

    let private wireToStatus (wire: string) =
        match wire with
        | "on" -> On
        | "off" -> Off
        | _ -> Unknown

    let private addParam (cmd: NpgsqlCommand) (name: string) (value: obj) =
        cmd.Parameters.AddWithValue(name, value) |> ignore

    /// Builds an Async<Result<...>> from a Task: wraps thrown exceptions as
    /// UpstreamUnavailable so the rest of the system stays Result-typed.
    let private guard (work: Task<'T>) : Async<Result<'T, StoreError>> =
        async {
            try
                let! v = work |> Async.AwaitTask
                return Ok v
            with ex ->
                return Error (UpstreamUnavailable (sprintf "postgres: %s" ex.Message))
        }

    type Store(connectionString: string) =

        member _.Record : Ports.RecordObservation =
            fun obs ->
                let work =
                    task {
                        use conn = new NpgsqlConnection(connectionString)
                        do! conn.OpenAsync()
                        use! tx = conn.BeginTransactionAsync()

                        let (StoreId sid) = obs.StoreId
                        let wire = statusToWire obs.Status

                        // Lock the row if it exists. If it doesn't, FOR UPDATE
                        // does nothing — the upcoming INSERT will create it.
                        use selectCmd =
                            new NpgsqlCommand(
                                "SELECT current_status FROM store_status \
                                 WHERE store_id = @id FOR UPDATE",
                                conn, tx)
                        addParam selectCmd "id" sid
                        let! existing = selectCmd.ExecuteScalarAsync()

                        let isFirst = isNull existing || existing = box DBNull.Value

                        let! outcome =
                            if isFirst then
                                task {
                                    use insertStatus =
                                        new NpgsqlCommand(
                                            "INSERT INTO store_status \
                                             (store_id, current_status, last_polled_at, \
                                              first_observed_at, last_flipped_at) \
                                             VALUES (@id, @s, @t, @t, NULL)",
                                            conn, tx)
                                    addParam insertStatus "id" sid
                                    addParam insertStatus "s" wire
                                    addParam insertStatus "t" obs.ObservedAt
                                    let! _ = insertStatus.ExecuteNonQueryAsync()

                                    use insertFlip =
                                        new NpgsqlCommand(
                                            "INSERT INTO flip_events \
                                             (store_id, observed_at, status) \
                                             VALUES (@id, @t, @s)",
                                            conn, tx)
                                    addParam insertFlip "id" sid
                                    addParam insertFlip "t" obs.ObservedAt
                                    addParam insertFlip "s" wire
                                    let! _ = insertFlip.ExecuteNonQueryAsync()
                                    return FirstObservation
                                }
                            else
                                let prev = wireToStatus (string existing)
                                if prev = obs.Status then
                                    task {
                                        // Status unchanged: refresh staleness sentinel only.
                                        use updateStatus =
                                            new NpgsqlCommand(
                                                "UPDATE store_status \
                                                 SET last_polled_at = @t \
                                                 WHERE store_id = @id",
                                                conn, tx)
                                        addParam updateStatus "id" sid
                                        addParam updateStatus "t" obs.ObservedAt
                                        let! _ = updateStatus.ExecuteNonQueryAsync()
                                        return Unchanged
                                    }
                                else
                                    task {
                                        use updateStatus =
                                            new NpgsqlCommand(
                                                "UPDATE store_status SET \
                                                   current_status = @s, \
                                                   last_polled_at = @t, \
                                                   last_flipped_at = @t \
                                                 WHERE store_id = @id",
                                                conn, tx)
                                        addParam updateStatus "id" sid
                                        addParam updateStatus "s" wire
                                        addParam updateStatus "t" obs.ObservedAt
                                        let! _ = updateStatus.ExecuteNonQueryAsync()

                                        use insertFlip =
                                            new NpgsqlCommand(
                                                "INSERT INTO flip_events \
                                                 (store_id, observed_at, status) \
                                                 VALUES (@id, @t, @s)",
                                                conn, tx)
                                        addParam insertFlip "id" sid
                                        addParam insertFlip "t" obs.ObservedAt
                                        addParam insertFlip "s" wire
                                        let! _ = insertFlip.ExecuteNonQueryAsync()
                                        return Flipped prev
                                    }

                        do! tx.CommitAsync()
                        return outcome
                    }
                guard work

        member _.History : Ports.GetHistory =
            fun (id, since, until) ->
                let work =
                    task {
                        use conn = new NpgsqlConnection(connectionString)
                        do! conn.OpenAsync()

                        let (StoreId sid) = id

                        // First confirm the store is known to us — otherwise an
                        // empty history is ambiguous (no flips? or no store?).
                        use exists =
                            new NpgsqlCommand(
                                "SELECT 1 FROM store_status WHERE store_id = @id",
                                conn)
                        addParam exists "id" sid
                        let! row = exists.ExecuteScalarAsync()
                        if isNull row || row = box DBNull.Value then
                            return Error (StoreNotFound id)
                        else
                            use cmd =
                                new NpgsqlCommand(
                                    "SELECT observed_at, status FROM flip_events \
                                     WHERE store_id = @id \
                                       AND observed_at >= @since \
                                       AND observed_at < @until \
                                     ORDER BY observed_at ASC",
                                    conn)
                            addParam cmd "id" sid
                            addParam cmd "since" since
                            addParam cmd "until" until
                            use! reader = cmd.ExecuteReaderAsync()
                            let observations = ResizeArray<HotLightObservation>()
                            let mutable hasNext = true
                            while hasNext do
                                let! r = reader.ReadAsync()
                                hasNext <- r
                                if hasNext then
                                    let observedAt = reader.GetFieldValue<DateTimeOffset>(0)
                                    let status = wireToStatus (reader.GetString 1)
                                    observations.Add {
                                        StoreId = id
                                        ObservedAt = observedAt
                                        Status = status
                                    }
                            return Ok (List.ofSeq observations)
                    }
                async {
                    try
                        let! r = work |> Async.AwaitTask
                        return r
                    with ex ->
                        return Error (UpstreamUnavailable (sprintf "postgres: %s" ex.Message))
                }

        member _.Status : Ports.GetStoreStatus =
            fun id ->
                let work =
                    task {
                        use conn = new NpgsqlConnection(connectionString)
                        do! conn.OpenAsync()
                        let (StoreId sid) = id

                        use cmd =
                            new NpgsqlCommand(
                                "SELECT current_status, last_polled_at, \
                                        first_observed_at, last_flipped_at \
                                 FROM store_status WHERE store_id = @id",
                                conn)
                        addParam cmd "id" sid
                        use! reader = cmd.ExecuteReaderAsync()
                        let! exists = reader.ReadAsync()
                        if not exists then
                            return Error (StoreNotFound id)
                        else
                            let status = wireToStatus (reader.GetString 0)
                            let lastPolled = reader.GetFieldValue<DateTimeOffset>(1)
                            let firstObserved = reader.GetFieldValue<DateTimeOffset>(2)
                            let lastFlipped =
                                if reader.IsDBNull 3 then None
                                else Some (reader.GetFieldValue<DateTimeOffset>(3))
                            return Ok {
                                StoreId = id
                                CurrentStatus = status
                                LastPolledAt = lastPolled
                                FirstObservedAt = firstObserved
                                LastFlippedAt = lastFlipped
                            }
                    }
                async {
                    try
                        let! r = work |> Async.AwaitTask
                        return r
                    with ex ->
                        return Error (UpstreamUnavailable (sprintf "postgres: %s" ex.Message))
                }

    let create (connectionString: string) = Store(connectionString)

    /// Postgres-backed push subscription store. Same connection string
    /// as the observations store, separate concern. UPSERT on
    /// (store_id, endpoint) means a re-subscribing browser refreshes
    /// its keys without creating a duplicate row.
    type PushSubscriptionsStore(connectionString: string) =

        member _.Subscribe : Ports.SubscribePush =
            fun (storeId, sub) ->
                let work =
                    task {
                        use conn = new NpgsqlConnection(connectionString)
                        do! conn.OpenAsync()
                        let (StoreId sid) = storeId

                        // ON CONFLICT DO UPDATE so RETURNING id always
                        // fires (DO NOTHING doesn't return on conflict).
                        // Refreshing the keys also handles the case where
                        // the browser rotated its push key material.
                        use cmd =
                            new NpgsqlCommand(
                                "INSERT INTO push_subscriptions \
                                   (store_id, endpoint, p256dh, auth) \
                                 VALUES (@id, @endpoint, @p256dh, @auth) \
                                 ON CONFLICT (store_id, endpoint) DO UPDATE \
                                   SET p256dh = EXCLUDED.p256dh, \
                                       auth   = EXCLUDED.auth \
                                 RETURNING id",
                                conn)
                        addParam cmd "id"       sid
                        addParam cmd "endpoint" sub.Endpoint
                        addParam cmd "p256dh"   sub.P256dh
                        addParam cmd "auth"     sub.Auth
                        let! result = cmd.ExecuteScalarAsync()
                        let id = downcast result : int64
                        return PushSubscriptionId id
                    }
                guard work

        member _.Unsubscribe : Ports.UnsubscribePush =
            fun (storeId, endpoint) ->
                let work =
                    task {
                        use conn = new NpgsqlConnection(connectionString)
                        do! conn.OpenAsync()
                        let (StoreId sid) = storeId

                        use cmd =
                            new NpgsqlCommand(
                                "DELETE FROM push_subscriptions \
                                 WHERE store_id = @id AND endpoint = @endpoint",
                                conn)
                        addParam cmd "id"       sid
                        addParam cmd "endpoint" endpoint
                        let! _ = cmd.ExecuteNonQueryAsync()
                        return ()
                    }
                guard work

        member _.FindForStore : Ports.FindPushSubscriptionsForStore =
            fun storeId ->
                let work =
                    task {
                        use conn = new NpgsqlConnection(connectionString)
                        do! conn.OpenAsync()
                        let (StoreId sid) = storeId

                        use cmd =
                            new NpgsqlCommand(
                                "SELECT id, endpoint, p256dh, auth, created_at \
                                 FROM push_subscriptions \
                                 WHERE store_id = @id \
                                 ORDER BY created_at ASC",
                                conn)
                        addParam cmd "id" sid
                        use! reader = cmd.ExecuteReaderAsync()
                        let result = ResizeArray<StoredPushSubscription>()
                        let mutable hasNext = true
                        while hasNext do
                            let! r = reader.ReadAsync()
                            hasNext <- r
                            if hasNext then
                                result.Add {
                                    Id = PushSubscriptionId (reader.GetInt64 0)
                                    StoreId = storeId
                                    Subscription = {
                                        Endpoint = reader.GetString 1
                                        P256dh   = reader.GetString 2
                                        Auth     = reader.GetString 3
                                    }
                                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(4)
                                }
                        return List.ofSeq result
                    }
                guard work

        member _.DeleteByEndpoint : Ports.DeletePushSubscriptionsByEndpoint =
            fun endpoint ->
                let work =
                    task {
                        use conn = new NpgsqlConnection(connectionString)
                        do! conn.OpenAsync()
                        use cmd =
                            new NpgsqlCommand(
                                "DELETE FROM push_subscriptions WHERE endpoint = @endpoint",
                                conn)
                        addParam cmd "endpoint" endpoint
                        let! affected = cmd.ExecuteNonQueryAsync()
                        return affected
                    }
                guard work

    let createPushSubscriptions (connectionString: string) =
        PushSubscriptionsStore(connectionString)
