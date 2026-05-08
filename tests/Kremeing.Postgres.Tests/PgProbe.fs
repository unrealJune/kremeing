module Kremeing.Postgres.Tests.PgProbe

open System
open Npgsql

/// The connection string tests use. Override via KREMEING_TEST_DATABASE_URL —
/// same env var shape we'll use in production for k8s. URL format is
/// accepted (postgres://user@host:port/db) or key=value Npgsql DSN.
///
/// Default targets a local Homebrew Postgres where the OS user owns the
/// database (peer auth, no password). CI sets the env var explicitly.
let connectionString =
    let defaultCs () =
        let user =
            match Environment.UserName with
            | null | "" -> "postgres"
            | u -> u
        sprintf "Host=localhost;Port=5432;Database=kremeing_test;Username=%s;Pooling=false" user

    match Environment.GetEnvironmentVariable "KREMEING_TEST_DATABASE_URL" with
    | null | "" -> defaultCs ()
    | s when s.StartsWith "postgres://" || s.StartsWith "postgresql://" ->
        // Npgsql parses URL-style natively when given to NpgsqlConnectionStringBuilder
        let csb = NpgsqlConnectionStringBuilder(s)
        csb.ToString()
    | s -> s

/// Quick liveness probe so tests skip cleanly if Postgres isn't running
/// instead of failing with cryptic connection refused stack traces.
let private isReachable =
    lazy
        try
            use conn = new NpgsqlConnection(connectionString)
            conn.Open()
            true
        with _ -> false

/// xUnit fact attribute that quietly skips when no Postgres is reachable
/// at the configured connection string.
type PgFactAttribute() =
    inherit Xunit.FactAttribute(
        Skip =
            (if isReachable.Value then null
             else sprintf "skipped: Postgres unreachable at '%s' \
                          (set KREMEING_TEST_DATABASE_URL or run brew services start postgresql@16)"
                          connectionString))
