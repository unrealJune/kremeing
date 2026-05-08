// VAPID keypair generator. Run once per environment, then put the
// output into your secret store (k8s Secret, .env, etc.).
//
//   dotnet fsi scripts/generate-vapid.fsx
//
// Output is the two env vars kremeing reads on startup:
//   KREMEING_VAPID_PUBLIC_KEY   — served to browsers via /vapid-public-key
//   KREMEING_VAPID_PRIVATE_KEY  — signs each push delivery; KEEP SECRET
//
// You can also set KREMEING_VAPID_SUBJECT to a mailto: or https: URL
// the browser push services can use to reach you about abuse. Defaults
// to `mailto:hotlight@kremeing.invalid` if unset.
//
// VAPID per RFC 8292 uses ECDSA on the NIST P-256 curve. The public
// key is the uncompressed point (0x04 || X32 || Y32 = 65 bytes), the
// private key is the 32-byte scalar D. Both are URL-safe base64 with
// no padding.

open System
open System.Security.Cryptography

let urlBase64 (bytes: byte[]) =
    Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_')

let ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256)
let p = ecdsa.ExportParameters(true)

let publicKey =
    let buf = Array.zeroCreate<byte> 65
    buf.[0] <- 0x04uy
    Array.Copy(p.Q.X, 0, buf, 1, 32)
    Array.Copy(p.Q.Y, 0, buf, 33, 32)
    urlBase64 buf

// `D` may be left-zero-padded to 32 bytes by some platforms; normalize.
let privateKey =
    let d = p.D
    let padded =
        if d.Length = 32 then d
        elif d.Length < 32 then
            let buf = Array.zeroCreate<byte> 32
            Array.Copy(d, 0, buf, 32 - d.Length, d.Length)
            buf
        else d.[d.Length - 32 ..]
    urlBase64 padded

printfn ""
printfn "# VAPID keypair — generated %s" (DateTime.UtcNow.ToString "u")
printfn ""
printfn "export KREMEING_VAPID_PUBLIC_KEY='%s'" publicKey
printfn "export KREMEING_VAPID_PRIVATE_KEY='%s'" privateKey
printfn "export KREMEING_VAPID_SUBJECT='mailto:you@example.com'   # change me"
printfn ""
printfn "# k8s shape (paste into a Secret manifest):"
printfn "#"
printfn "# stringData:"
printfn "#   vapid-public-key:  '%s'" publicKey
printfn "#   vapid-private-key: '%s'" privateKey
printfn ""
