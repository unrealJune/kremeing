namespace Kremeing.Core

open Kremeing.Contracts.Domain

module Validation =

    let parseStoreId (raw: string) : Result<StoreId, StoreError> =
        if isNull raw then
            Error (InvalidStoreId "")
        else
            let trimmed = raw.Trim()
            match System.Int32.TryParse trimmed with
            | true, n when n > 0 -> Ok (StoreId n)
            | _ -> Error (InvalidStoreId raw)
