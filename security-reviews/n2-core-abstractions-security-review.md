# Security review: N2.Core.Abstractions

Review date: 2026-11-21. Reviewed: default branch of `Kaalenco/n2-core-abstractions` (csproj `1.7.0`, targets `netstandard2.0;netstandard2.1;net8.0;net10.0`).
Method: manual read of all non-test source under `src/N2.Core.Abstractions`, `scripts/` and `.github/workflows/`. Nothing was built, run or fuzzed, and no SCA tool was available (no .NET SDK in the review environment).
Severity scale: **High** (exploitable or secret-exposing by default), **Medium** (exploitable under common usage), **Low** (hardening).

## Summary

This package is mostly interfaces and DTOs, so its attack surface is small.
The one real security component is `Encryption/AuthenticatedValue` (+ `EncryptionUtilities`). The core design is sound: AES-256-CBC, encrypt-then-MAC, MAC verified before decrypting, domain-separated keys, fixed-time compare, mandatory expiry.
The findings are about hardening that component, plus a few types that make it easy for callers to leak secrets, and the release pipeline.

| ID | Sev | Area | Title |
|---|---|---|---|
| A-1 | Medium | Encryption | No purpose/context binding: a token for one use is valid for any other use of the same secret |
| A-2 | Medium | Encryption | Key derivation uses a bare HMAC of the raw secret, with no salt and no key id, so there is no rotation path |
| A-3 | Low | Encryption | Expiry parsing: `DateTime` kind, endianness and tick range handling |
| A-4 | Low | Encryption | Distinguishable failure messages and exception types |
| A-5 | Low | Encryption | Hand-rolled constant-time compare, key material never cleared |
| A-6 | Medium | Identity | `Token` serialises `client_secret`, `access_token`, `refresh_token` by default |
| A-7 | Low | Commands | Exception/response types carry free-text and request echoes that callers may expose |
| A-8 | Low | Commands | `Activator.CreateInstance(GetType())` in `CommandResponse` |
| A-9 | Medium | Pipeline | Publish pipeline: third-party secrets action, unpinned actions, `**/*.nupkg` glob |
| A-10 | Low | Scripts | Secret generator scripts print secrets to the console and use inconsistent sizes |
| A-11 | Low | Supply chain | Frameworks and dependencies: legacy `netstandard2.0` crypto branch, dependency pins |

---

## Findings

### A-1 (Medium): no purpose binding in `AuthenticatedValue`
`ToTokenString(systemSecret)` / `FromTokenString(...)` use one secret for every token type.
A token minted for "email confirmation" is accepted wherever "password reset" or "MFA session" tokens are verified with the same secret, because nothing in the token says what it is for. Any code path that accepts a token string and that can be fed another feature's token is a confused-deputy risk.
This is the classic problem in home-grown "protected value" APIs. ASP.NET's Data Protection API solves it with a purpose string.

**Fix.** Add a mandatory `purpose` (or `context`) parameter, include it in the key derivation labels (`"N2.AuthenticatedValue.v1.enc|" + purpose`), or authenticate it as associated data in the MAC input. Keep the old overloads as `[Obsolete]` and call them with a fixed legacy purpose, so existing tokens stay valid for one release.
Add tests: a token created for purpose A must fail for purpose B.

### A-2 (Medium): key derivation and rotation
`DeriveKeys` does `HMACSHA256(systemSecret).ComputeHash(label)`. That is a valid one-step KDF only if `systemSecret` is already a uniformly random 256-bit key. The only check is `Length >= 32`, so a 32-character ASCII passphrase, an all-zero array or a short string repeated is accepted and gives weak keys.
There is also no key identifier in the wire format, so rotating the secret invalidates every outstanding token at once and cannot be done gradually.

**Fix.**
- Use `HKDF` (`System.Security.Cryptography.HKDF`, available on net5+ and as a package on netstandard) with an info string, and document that the secret must be random (generate with `EncryptionUtilities.GenerateRandomKey(32)`).
- Add a 1-byte version and 1-byte key id to the header and include both in the MAC input. Accept a list of secrets in `FromTokenString` (current plus previous), so keys can rotate.
- Reject trivially weak secrets (all zero, fewer than 8 distinct bytes).

### A-3 (Low): expiry encoding
- `new DateTime(ticks, DateTimeKind.Utc)` throws `ArgumentOutOfRangeException` for out-of-range ticks. This can only happen after MAC verification, so it is not attacker-reachable, but a buggy issuer could produce it. Wrap and throw `FormatException`.
- `ToUniversalTime()` on a `DateTimeKind.Unspecified` value treats it as **local time**, so the effective expiry depends on the server's time zone. Convert explicitly, or take a `DateTimeOffset`.
- `BitConverter` is endianness-dependent. Use `BinaryPrimitives` (little-endian) so tokens are portable between machines.
- Consider a small maximum lifetime check (`expiration - now <= MaxLifetime`) on both create and verify to catch `DateTime.MaxValue` "never expires" tokens.
- The expiry check has no clock-skew allowance. That is fine for same-host use, but document it for multi-node deployments.

### A-4 (Low): failure oracle
`FromTokenString` throws distinct exceptions and messages for "malformed base64", "too short", "MAC mismatch", "expired" and "plaintext length mismatch". The MAC is checked before decryption, so there is **no padding oracle** (the code is right about that). The remaining distinguishable outcomes tell an attacker whether a forged string is well-formed and whether a *valid* token has expired (only if they hold a valid token).
Mitigation: callers must map every `FormatException`/`CryptographicException` from this API to one generic client error. Add a `TryFromTokenString(out AuthenticatedValue?)` that never throws on bad input, and document it as the preferred entry point on request paths.

### A-5 (Low): constant-time compare and key hygiene
- `ConstantTimeEquals` is hand-written. Use `CryptographicOperations.FixedTimeEquals` (net core 2.1+, netstandard2.1+). Keep the loop only for the `netstandard2.0` branch.
- `encKey`, `macKey`, the plaintext buffer and the derived arrays are never zeroed. Use `CryptographicOperations.ZeroMemory` in `finally`. This is defence in depth only, since .NET gives no strong guarantee here.
- The `#pragma warning disable CA5401` blocks are justified (the IV is random), but note that `aes.IV = iv` on decrypt is also the CA5401 case, so keep the comment accurate.
- Consider AES-GCM (`AesGcm`) instead of CBC+HMAC on net8+: it removes the manual Encrypt-then-MAC code. If you keep CBC, the current construction is correct.

### A-6 (Medium): `Identity.Token` leaks secrets by default
`Token` has `ClientSecret`, `AccessToken` and `RefreshToken` as plain properties, and `Serialize()` writes them all. `ToString()` is not overridden, so a debugger or `logger.LogInformation("{Token}", token)` prints the type name, but anything that serializes the object (the core library does this for request logging, see the n2-core review, C-1) writes live credentials to logs.
Note that `ClientSecret` is also a **request** field, whereas the others are response fields. A single class doing both invites accidental echo of a client secret in a response.

**Fix.**
- Split into `TokenRequest` payload and `TokenResponse` payload types (breaking, so for the next major).
- At minimum add `[JsonIgnore]`-by-default redaction: a `Redacted()` method, an overridden `ToString()`, and a `[SensitiveData]`/`ProtectedString` wrapper that serializes as `"***"`.
- Document that `ExpiresIn` here is an absolute UNIX timestamp, while RFC 6749 defines `expires_in` as seconds from now. A standard OAuth client will compute an expiry in 2055. Rename the property or the JSON name.
- `Token.Deserialize(json)` on untrusted input returns `new Token()` for the JSON `null`, and has no size limit. Use `JsonSerializerOptions.MaxDepth` and cap the input length at the caller.

### A-7 (Low): response/exception types make leaking easy
`N2CoreException(string message)` and `CommandResponse.Message` are free-text and flow to API clients in the core library (see the n2-core review, C-2/C-3). The abstraction does not distinguish "safe for the client" text from internal diagnostics.
**Fix.** Add `ClientMessage` vs `Message` (or an `IsSafeToExpose` flag) so handlers must state what may leave the process.

### A-8 (Low): `Activator.CreateInstance(GetType())`
`CommandResponse.CreateNew` and `Clone` create instances by reflection on the runtime type. This requires a public parameterless constructor (a derived type without one throws `MissingMethodException`, which is a runtime failure) and is not trimming/AOT-safe (IL2072). Not attacker-reachable, but it is a reliability and trimming problem for consumers.
**Fix.** Make `CreateNew` abstract/virtual and implement it per derived type, or use a `static abstract` factory (net7+).

### A-9 (Medium): release pipeline (`.github/workflows/dotnet.yml`)
This repo has a more mature workflow than n2-core: conditional publish, artifacts, CodeQL, and a maintenance job. Remaining issues:
- `Firenza/secrets-to-env@v1.3.0` receives `toJSON(secrets)`, so a third-party action sees **every** repository secret and exports them as environment variables for all later steps (including `dotnet build`/`test`, which run repository code). Remove it, and pass only the one secret the publish step needs via `env:`.
- Actions are pinned to tags (`@v4`, `@v5`, `@v6`), and `checkout`/`setup-dotnet` use mixed major versions. Pin by commit SHA and let Dependabot update them.
- `dotnet nuget push **/*.nupkg` is unquoted and has no `--skip-duplicate`.
- Set top-level `permissions: contents: read`, and give only the jobs that need it more (CodeQL `security-events: write`).
- Make sure publishing is limited to protected branches or tags, and that the NuGet API key is scoped to this package only. `scripts/unlist-old-nuget-versions.sh` needs an *unlist* scope. Use a separate key for it.
- `scripts/delete-workflow-runs.sh` requires a fine-grained PAT with **Actions: write**. Store it as an environment secret, expire it, and run the maintenance workflow only on `schedule`/`workflow_dispatch`, never on `pull_request`.

### A-10 (Low): helper scripts
- `scripts/Generate-Secrets.ps1` prints secrets with `Write-Host` to the console and shell history/CI logs if redirected. The default behaviour should be to write to the clipboard or directly to `dotnet user-secrets`. It also advertises `AES-256-GCM` for `MfaTokenSecret`, but the library's token format is AES-256-CBC+HMAC. Align the docs. The header comment promises piping to `user-secrets set`, but the output is human text, so it cannot be piped.
- `generate_token_secret.py` produces 32 random bytes (fine). `Generate-Secrets.ps1` uses `RandomNumberGenerator.GetBytes(32)`, which requires PowerShell 7 / .NET 6+. The `#Requires -Version 5.1` header is wrong, because Windows PowerShell 5.1 (.NET Framework) does not have the static `GetBytes(int)`.
- `JwtTools.ConvertToJwt` in n2-core and the README say 64-byte keys (`CreateRandomKey`), but these scripts give 32. Both are acceptable for HS256, so pick one number and document it.

### A-11 (Low): dependencies and frameworks
- Packages checked on 2026-11-21: `System.IO.Abstractions` 22.1.0 (latest 23.0.1), `System.Text.Json` and `System.Text.Encodings.Web` 10.0.5 (latest 11.0.0), `Microsoft.Extensions.Primitives` 10.0.5 (latest 11.0.0). No advisories were checked, because there is no SDK here: run `dotnet list package --vulnerable --include-transitive` in CI (`NuGetAudit` is already on with `All`/`Low`). Turn the relevant `NU190x` warnings into errors.
- Targets: .NET 8 reaches end of support in November 2026, so consider dropping `net8.0` and adding `net11.0`. The `netstandard2.0` target keeps the old crypto branch (`RNGCryptoServiceProvider`, `SYSLIB0023`).
- Several `#if` checks use `NET8_0_OR_GREATER` for the CSPRNG path. This is fine, but on `netstandard2.1` the `#else` branch with `RNGCryptoServiceProvider` is taken, so make sure that branch is tested on all four targets.

---

## Things done well (keep)
- Encrypt-then-MAC with MAC verified *before* any decryption and before parsing.
- Independent encryption and MAC keys via domain separation; random IV per token from a CSPRNG.
- Length checks before slicing (`< IvLength + 16 + MacLength`), plaintext length consistency check.
- `Expiration` is mandatory and normalised to UTC; expired tokens are rejected after authentication.
- `NuGetAudit` on, warnings as errors, CodeQL workflow present.

## Suggested order of work
1. A-6 (token redaction) and A-9 (remove `secrets-to-env`): low effort, immediate exposure reduction.
2. A-1 and A-2 together as one token-format `v2` (purpose, HKDF, key id, `TryFromTokenString`), with a compatibility reader for `v1`.
3. A-3..A-5 hardening inside the same change.
4. A-8, A-10, A-11 clean-up.
