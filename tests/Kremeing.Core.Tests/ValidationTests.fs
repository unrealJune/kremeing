module Kremeing.Core.Tests.ValidationTests

open Xunit
open Kremeing.Contracts.Domain
open Kremeing.Core

// Equality assertions on F# DUs use Xunit's Assert.Equal, which uses
// structural equality. FsUnit's NHamcrest-based `should equal`
// misbehaves on nested DUs like Result<StoreId, StoreError>.

[<Fact>]
let ``parseStoreId rejects empty string`` () =
    Assert.Equal(Error (InvalidStoreId ""), Validation.parseStoreId "")

[<Fact>]
let ``parseStoreId rejects whitespace-only input`` () =
    Assert.Equal(Error (InvalidStoreId "   "), Validation.parseStoreId "   ")

[<Fact>]
let ``parseStoreId rejects null and reports it as empty raw value`` () =
    Assert.Equal(Error (InvalidStoreId ""), Validation.parseStoreId null)

[<Fact>]
let ``parseStoreId rejects non-numeric input`` () =
    Assert.Equal(Error (InvalidStoreId "issaquah"), Validation.parseStoreId "issaquah")

[<Fact>]
let ``parseStoreId rejects zero`` () =
    // shopIds upstream are always positive; zero indicates a bug, not a real store
    Assert.Equal(Error (InvalidStoreId "0"), Validation.parseStoreId "0")

[<Fact>]
let ``parseStoreId rejects negative ids`` () =
    Assert.Equal(Error (InvalidStoreId "-12"), Validation.parseStoreId "-12")

[<Fact>]
let ``parseStoreId accepts a normal positive integer`` () =
    Assert.Equal(Ok (StoreId 899), Validation.parseStoreId "899")

[<Fact>]
let ``parseStoreId tolerates surrounding whitespace`` () =
    Assert.Equal(Ok (StoreId 899), Validation.parseStoreId "  899  ")
