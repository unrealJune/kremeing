module Kremeing.Core.Tests.MappingTests

open System
open Xunit
open FsUnit.Xunit
open Kremeing.Contracts.Domain
open Kremeing.Contracts.Api
open Kremeing.Core

module StatusToWire =
    [<Theory>]
    [<InlineData("on")>]
    [<InlineData("off")>]
    [<InlineData("unknown")>]
    let ``every wire value is lowercase ASCII`` (wire: string) =
        // Property: outputs are stable lowercase strings — this is
        // the contract for downstream JSON consumers.
        wire |> should equal (wire.ToLowerInvariant())

    [<Fact>]
    let ``On maps to "on"`` () =
        Mapping.statusToWire On |> should equal "on"

    [<Fact>]
    let ``Off maps to "off"`` () =
        Mapping.statusToWire Off |> should equal "off"

    [<Fact>]
    let ``Unknown maps to "unknown"`` () =
        Mapping.statusToWire Unknown |> should equal "unknown"

module StoreToDto =
    let sampleStore = {
        Id = StoreId 899
        Name = "Krispy Kreme Seattle - 1st Ave South"
        Address = "1900 1st Ave S, Seattle, WA 98134"
        Location = { Latitude = 47.58536; Longitude = -122.33407 }
    }

    [<Fact>]
    let ``id is the upstream shopId integer`` () =
        (Mapping.storeToDto sampleStore).id |> should equal 899

    [<Fact>]
    let ``name passes through unchanged`` () =
        (Mapping.storeToDto sampleStore).name |> should equal sampleStore.Name

    [<Fact>]
    let ``coordinates are split into separate latitude and longitude fields`` () =
        let dto = Mapping.storeToDto sampleStore
        dto.latitude |> should equal 47.58536
        dto.longitude |> should equal -122.33407

module ObservationToDto =
    [<Fact>]
    let ``observedAt is preserved exactly`` () =
        let ts = DateTimeOffset(2026, 5, 7, 12, 30, 0, TimeSpan.Zero)
        let obs = { StoreId = StoreId 899; Status = On; ObservedAt = ts }
        (Mapping.observationToDto obs).observedAt |> should equal ts

    [<Fact>]
    let ``status is wire-encoded, not the discriminator name`` () =
        let obs = {
            StoreId = StoreId 899
            Status = On
            ObservedAt = DateTimeOffset.UtcNow
        }
        (Mapping.observationToDto obs).status |> should equal "on"

    [<Fact>]
    let ``storeId is the upstream shopId integer`` () =
        let obs = {
            StoreId = StoreId 898
            Status = Off
            ObservedAt = DateTimeOffset.UtcNow
        }
        (Mapping.observationToDto obs).storeId |> should equal 898

module ErrorToDto =
    [<Fact>]
    let ``StoreNotFound carries the offending id in the message`` () =
        let dto = Mapping.errorToDto (StoreNotFound (StoreId 12345))
        dto.error |> should equal ErrorCodes.StoreNotFound
        dto.message |> should haveSubstring "12345"

    [<Fact>]
    let ``UpstreamUnavailable surfaces the reason verbatim`` () =
        let dto = Mapping.errorToDto (UpstreamUnavailable "krispykreme.com timed out")
        dto.error |> should equal ErrorCodes.UpstreamUnavailable
        dto.message |> should equal "krispykreme.com timed out"

    [<Fact>]
    let ``InvalidStoreId echoes the raw input`` () =
        let dto = Mapping.errorToDto (InvalidStoreId "   ")
        dto.error |> should equal ErrorCodes.InvalidStoreId
        dto.message |> should haveSubstring "   "

module ErrorToStatusCode =
    [<Fact>]
    let ``StoreNotFound is 404`` () =
        Mapping.errorToStatusCode (StoreNotFound (StoreId 1)) |> should equal 404

    [<Fact>]
    let ``UpstreamUnavailable is 502`` () =
        Mapping.errorToStatusCode (UpstreamUnavailable "boom") |> should equal 502

    [<Fact>]
    let ``InvalidStoreId is 400`` () =
        Mapping.errorToStatusCode (InvalidStoreId "") |> should equal 400
