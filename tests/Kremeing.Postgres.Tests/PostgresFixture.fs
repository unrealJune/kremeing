module Kremeing.Postgres.Tests.PostgresFixture

open System.Threading.Tasks
open Npgsql
open Xunit
open Kremeing.Api

/// One shared Postgres connection per test class. Schema is applied
/// once at class init; each test calls Reset() to truncate state.
/// Connection string comes from PgProbe (env-driven, k8s-style).
type PostgresFixture() =

    let connectionString = PgProbe.connectionString
    let mutable initialized = false

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task {
                // Skip schema creation if Postgres isn't reachable —
                // PgFact-skipped tests will never call ConnectionString.
                if PgProbe.PgFactAttribute().Skip = null then
                    do! Postgres.applySchema connectionString
                    initialized <- true
            } :> Task

        member _.DisposeAsync() = Task.CompletedTask :> Task

    member _.ConnectionString : string =
        if not initialized then
            failwith "PostgresFixture.ConnectionString accessed before Postgres became reachable"
        connectionString

    /// Wipes both tables so each test starts clean. Cheaper than
    /// dropping the schema; same isolation guarantee.
    member this.Reset() : Task =
        task {
            use conn = new NpgsqlConnection(this.ConnectionString)
            do! conn.OpenAsync()
            use cmd = new NpgsqlCommand(
                        "TRUNCATE TABLE flip_events, store_status RESTART IDENTITY",
                        conn)
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }
