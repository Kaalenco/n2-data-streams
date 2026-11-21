# Handover: N2.DataStreams review and remediation

Review date: 2026-11-21. Reviewed commit: `e0a5152` on `trunk`.
Compared against: `Kaalenco/n2-core` (`src/N2.Core`, `.github/workflows/dotnet.yml`, `src/Directory.Build.props`).

**Limits of this review.** The review environment has no .NET SDK. Nothing was built or tested.
Findings come from reading the source and querying the nuget.org API.
Compiler-error predictions for net10/net11 are expected, not observed. Run the build first (item 0).

## Decisions already made (with the owner)

| Topic | Decision |
|---|---|
| Target frameworks | Library: `net10.0;net11.0` only. This drops net8.0 **and** netstandard2.0. It is a breaking change, so bump to **2.0.0**. |
| AS2 code (`A2Protocol/`) | Harden in place (section C), not remove. |
| CI/publish | Align with n2-core and fix the gaps n2-core also has (section B). |

The unit tests should target `net10.0;net11.0` too, because they run against the same TFMs.

Open item: the project is named `A2Protocol`, but the protocol is AS2 (RFC 4130). Decide whether to rename the namespace in 2.0.0. The default recommendation is yes, since the 2.0.0 break is the cheapest time to do it.

---

## 0. First actions (order matters)

1. Install SDK 10 and 11 locally. Run `dotnet build src -c Release` and `dotnet test src` on the current `trunk` to get a baseline.
2. Apply section A (frameworks and packages), then fix the compile errors the new TFMs produce (see C-1). Expect some.
3. Apply section B (CI), then the stability fixes (D), then the AS2 hardening (C).
4. Run `dotnet list src package --outdated --include-transitive` and `dotnet list src package --vulnerable --include-transitive`. I could not run these.

---

## A. Packages and frameworks

### A-1. Package status (checked against nuget.org on 2026-11-21)

| Package | In repo | Latest stable | Action |
|---|---|---|---|
| System.Security.Cryptography.Pkcs | 8.0.1 | 11.0.0 | Remove the reference if it is in the shared framework on net10/net11 (verify with a build). Otherwise update to 11.0.0. |
| System.IO.Abstractions | 21.0.29 | 23.0.1 | Update (n2-core uses 22.1.0, so move both repos forward). |
| System.Text.Json | 8.0.5 (lib and tests) | 11.0.0 | **Remove.** It is inbox on net10/net11. |
| System.Net.Http | 4.3.4 | 4.3.4 (legacy) | **Remove.** It is legacy and inbox. |
| System.Net.Requests | 4.3.0 | 4.3.0 (legacy) | **Remove.** It is legacy and inbox. |
| Microsoft.Extensions.Features | 8.0.10 | 11.0.0 | **Remove.** Nothing in the code uses it (grep confirms). |
| Microsoft.Extensions.Http.Polly | 8.0.10 | 11.0.0 | **Remove.** Nothing uses Polly, and the package is deprecated in favour of `Microsoft.Extensions.Http.Resilience`. It also pulls a large transitive tree for nothing. |
| Microsoft.NET.Test.Sdk | 17.11.1 | 18.12.0 | Update. |
| MSTest.TestAdapter / TestFramework | 3.6.2 | 4.5.3 | Update (major). MSTest 4 has breaking changes. `[DataTestMethod]` and some `Assert` overloads are the likely hits. Consider the `MSTest` metapackage. |
| Moq | 4.20.72 | 4.21.0 | Update. |
| coverlet.collector | 6.0.2 | 10.1.1 | Update. |

Removing five libraries of dead dependencies is also the main security win in this section. The legacy 4.3.x System.Net.* packages have a long CVE history.

### A-2. `N2.DataStreams.csproj` clean-up

- `<TargetFramework>netstandard2.0</TargetFramework>` becomes `<TargetFrameworks>net10.0;net11.0</TargetFrameworks>`.
- `LangVersion` is declared twice and pinned to 12. Remove both pins. The defaults (14 on net10, 15 on net11) are right.
- Fix the stale metadata. `Product` says "for NET8.0", `ProductVersion` is 1.0.0 while `PackageVersion` is 1.0.2, and `Copyright` says 2024.
  n2-core sets `Version`, `ProductVersion` and `PackageVersion` together. Prefer a single `<Version>2.0.0</Version>` and drop the other two.
- Add the NuGet audit block n2-core has:
  ```xml
  <NuGetAudit>true</NuGetAudit><NuGetAuditMode>All</NuGetAuditMode><NuGetAuditLevel>Low</NuGetAuditLevel>
  ```
  Also set `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` and `<WarningsAsErrors>NU1901;NU1902;NU1903;NU1904</WarningsAsErrors>` so a vulnerable dependency fails the build.
- Consider adding `<IsTrimmable>` / `<EnableAotAnalyzer>` only after fixing the reflection-based JSON (D-9).
- Add SourceLink, a snupkg and `<Deterministic>` + `<ContinuousIntegrationBuild>` on CI. n2-core does not do this either, but it costs little. This is optional.

### A-3. Test project

- `TargetFrameworks`: `net8.0` becomes `net10.0;net11.0`.
- Remove the `System.Text.Json` reference and the `LangVersion` pin.
- Remove the `System.IO.Abstractions` reference only if it is already transitive through the project reference. Otherwise update it.

---

## B. Publication setup: comparison with n2-core, and gaps

n2-core and n2-data-streams use the **same** pipeline, so the gaps are shared. Fix them here. Raise the n2-core ones separately.

| Aspect | n2-core | n2-data-streams | Verdict |
|---|---|---|---|
| Trigger | push/PR to `trunk` | push/PR to `trunk` | Same |
| Packaging | `GeneratePackageOnBuild` | `GeneratePackageOnBuild` | Same, and fragile (B-3) |
| Publish step | `dotnet nuget push **/*.nupkg --api-key ${{ secrets.PACKAGE_N2 }}` | same | Same, with gaps (B-1, B-2) |
| SDKs in CI | 8 and 9 (but the library targets `NET10.0`) | 8 only | Both outdated |
| `Directory.Build.props` | Yes: analyzers, `AnalysisMode=All`, warnings as errors | **None** | Missing |
| `nuget.config` | Yes (nuget.org only) | **None** | Missing |
| NuGet audit | Enabled | **Not enabled** | Missing |
| Coding-style / CONTRIBUTING / copilot instructions | Yes | None | Optional |

### B-1. Publishing runs on pull requests
The `Publish` step has no `if:` guard. Any same-repo PR that bumps nothing still tries to push a package.
Add `if: github.event_name == 'push' && github.ref == 'refs/heads/trunk'`, or move publishing to a separate job triggered by a version tag (`v*`) with `needs: build`.

### B-2. Every push without a version bump fails (or worse)
There is no `--skip-duplicate`, so every `trunk` push that does not change the version fails the pipeline at the push step with a 409.
Add `--skip-duplicate`. Pushing to nuget.org is also irreversible (a version can be unlisted but never reused), so a tag-gated release is safer.

### B-3. Packing and building in Debug
`dotnet build src --no-restore` builds Debug, and `GeneratePackageOnBuild` packs that Debug output, which is then published.
Replace it with `dotnet build -c Release`, `dotnet test -c Release --no-build`, then an explicit `dotnet pack src/N2.DataStreams -c Release --no-build -o ./artifacts`.
Remove `GeneratePackageOnBuild` and push `./artifacts/*.nupkg`. Quote the glob so the shell does not expand it.
With `GeneratePackageOnBuild`, a plain local build also drops packages into `bin/`, which `**/*.nupkg` may pick up.

### B-4. SDK setup
Replace `dotnet-version: 8.0.x` with both SDKs (`10.0.x` and `11.0.x`). Use `dotnet-quality: ga` unless .NET 11 is still preview on the runner, in which case allow `preview` for 11 only.
Keep the library and test TFMs identical so every TFM is actually tested.

### B-5. Workflow hardening
- Add `permissions: contents: read` at the top level. The job needs nothing else.
- Pin `actions/checkout` and `actions/setup-dotnet` to commit SHAs (or at least review at each major bump). They are on `@v4` now.
- Add `concurrency` to stop two pushes racing to publish.
- Do not copy n2-core's `Firenza/secrets-to-env` step. It is a third-party action that receives `toJSON(secrets)`, which is every secret in the repo. This repo does not use it, so keep it that way.
- Use a nuget.org API key scoped to this package only and rotate it. Check that `PACKAGE_N2` is not shared across all packages.

### B-6. Repo files to add (mirroring n2-core)
`src/Directory.Build.props` (analyzers, warnings as errors, `Nullable`, `ImplicitUsings`, `AnalysisMode`), `src/nuget.config` (nuget.org source only, optionally with package source mapping), and a README section on supported TFMs and how a release is cut.

---

## C. AS2 / `A2Protocol` hardening (security-critical)

**Status of the code today.** The commit message says "Fix build for A2 prtocol (not tested)", and there are no tests. It is a port of a 2010 blog sample.
**It should not be shipped as production-ready until every Critical and High item below is closed and covered by tests.**
If that is not feasible for the 2.0.0 release, ship it marked `[Experimental]` (or `[Obsolete]`) as an interim step. The owner chose "harden in place", so the fixes follow.

### Status (updated after the AS2 rewrite)

Done and covered by tests (`AS2Should`, 44 test cases, all passing on net10; net11 is verified by CI only):
- C-1 compile errors: `X509CertificateLoader`, `HttpClient`, nullability.
- C-2 #1 path traversal: nothing from the message is a file name. Files get a generated name, are created with `CreateNew`, and the resolved path is checked against the drop directory.
- C-2 #2 signatures: verified with `SignedCms`, and pinned to the registered partner certificate (exact match, validity dates checked). Unsigned messages are rejected by default.
- C-2 #3 size and partial reads: a configurable maximum message size (413), no allocation from `Content-Length`. The host reads the body into `AS2ReceivedMessage`.
- C-2 #4 `DmnMessage`: **removed**, including its hardcoded placeholders. MDN generation is *not* implemented (see below).
- C-3 #5 `Decrypt` returned the wrong data: now returns `ContentInfo.Content`; round-trip tests for AES-256, AES-128 and 3DES.
- C-3 #6 crypto: SHA-256 signing with `micalg="sha-256"`, AES-256 default, 3DES only as an explicit legacy option (receivers reject it unless `AllowLegacyEncryption`), RC2 removed. `EncryptionAlgorithm` values are now `const`.
- C-3 #7 parsing: bounds-checked byte-exact parser, returns `false` instead of throwing; malformed input gives 400/401.
- C-3 #8 line endings: CRLF everywhere.
- C-3 #9 `GetMessage`/`BadRequest` writing JSON+deflate: removed with the old receive classes.
- C-3 #10 sender: unique `Message-Id`, no automatic redirects, https required (opt-out for local tests), validated header values and file names, `HttpClient` + `CancellationToken`.
- C-3 #11 listener: `A2Listener`/`IHttpContext`/`AS2Receive` replaced by `AS2Receiver` (host-agnostic, takes an `AS2ReceivedMessage`).
- C-4 #12 (partly), #13 payload is bytes end to end, #14 no HTML is produced, #16 replay protection: Message-Ids are remembered for `DuplicateWindow` (in memory, per process).

**Breaking API changes (2.0.0):** `AS2Send.SendFile(...)` becomes `SendFileAsync(HttpClient, AS2SendOptions, ...)`; `AS2Receive`, `A2Listener`, `IHttpContext` and `DmnMessage` are gone; `ProxySettings` is a class; `EncryptionAlgorithm.RC2` is gone; certificates are passed as `X509Certificate2` objects.

Still open:
- **MDN (receipt) generation and verification is not implemented.** Partners that require a signed MDN will not accept this receiver yet. The sender does not process the partner's MDN either, so a 200 means "HTTP accepted", not "partner processed it".
- The duplicate-detection store is in memory. Across restarts or several instances, back it with shared storage.
- Certificate chain validation is deliberately replaced by pinning. Certificate revocation is not checked.
- Logging (`ILogger`), rate limiting and request authentication beyond the signature are left to the host.
- An ASP.NET Core endpoint sample (read the body with a size limit, call `AS2Receiver.Process`, return `StatusCode`/`Reason`).

### C-1. Will not compile cleanly on net10/net11 (warnings are errors here)
- `HttpWebRequest`, `WebRequest.Create` and `WebProxy` usage: SYSLIB0014 (obsolete). Rewrite `AS2Send` on `HttpClient` (injected, with `SocketsHttpHandler`/`IHttpClientFactory`) and make it async with `CancellationToken`.
- `new X509Certificate2(path[, password])` in `AS2Encryption`: SYSLIB0057 (obsolete since .NET 9). Use `X509CertificateLoader.LoadCertificateFromFile` and `LoadPkcs12FromFile` (with `X509KeyStorageFlags.EphemeralKeySet`). Dispose the certificate.
- Nullable annotations are not applied on netstandard2.0, but they are on net10. Expect CS8600/CS8602/CS8604 on `request.Headers[...]`, `ContentType.Contains`, `QueryString[0]`, etc. Fix them for real, not with `!`.
- Remove `System.Net.Requests` and `System.Net.Http` references (A-1).

### C-2. Critical

1. **Arbitrary file write / path traversal** (`AS2Receive.Process`). `File.WriteAllText(dropLocation + filename, message)` uses the attacker-controlled `Subject` header as the file name and concatenates without a separator.
   A `Subject` of `..\..\x` or an absolute path writes anywhere the process can write, and existing files are overwritten.
   Fix: never use the header as a path. Generate the file name (GUID plus a validated, allow-listed extension), build the path with `Path.Combine`, resolve with `Path.GetFullPath`, and verify the result is under the canonical drop directory. Use `FileMode.CreateNew`.
2. **No authentication of sender and no signature verification** (`ExtractPayload` literally says "Ignores signatures"). Anyone who sets `AS2-From`/`AS2-To` and a `multipart/signed` content type is accepted.
   Fix: verify with `SignedCms.CheckSignature` against a configured partner certificate (not self-signed trust-all). Check the partner identity, `AS2-From` and `AS2-To` against a configured allow-list, validate the certificate chain and expiry, and reject unsigned messages unless explicitly configured.
3. **Attacker-controlled allocation** (`new byte[request.ContentLength]`). A huge `Content-Length` gives OOM (DoS) and `-1` throws. A single `Read` call also truncates silently on partial reads.
   Fix: enforce a configurable maximum, read in a loop to a bounded buffer, reject on mismatch.
4. **Hard-coded placeholder secrets and dead code in `DmnMessage`**: `"some path"`, `"take it from app config"`, `"XXXXXXXXXX"`. It is also broken: header names contain spaces (`"AS2 - From"`, `"Mime - Version"`, `"Content - Type"`), the date format is wrong (`"yyy HH: mm:ss"`), and `WriteAsync` is not awaited.
   It also treats a client `WebRequest` as a server request and calls `request.GetResponse()`, which sends a new HTTP call.
   Fix: rewrite the MDN generation from RFC 4130 / RFC 3798 using the correct headers, take certificates through an injected options object, or delete the method until implemented. Never ship the placeholders.

### C-3. High

5. **`Decrypt` returns the wrong data** (`AS2Encryption.Decrypt` and `AS2Utilities.Decrypt`). After `envelopedCms.Decrypt()` they return `envelopedCms.Encode()`, which is the still-enveloped CMS blob. The decrypted payload is `envelopedCms.ContentInfo.Content`.
   So the receive path for encrypted messages cannot work today. Add round-trip tests.
6. **Weak or mismatched crypto.** Only `3DES` and `RC2` are offered (Sweet32 / obsolete; RC2 is unsupported on many platforms), and `micalg="sha1"` is announced while `CmsSigner` actually signs with SHA-256 by default. Partners will fail verification.
   Fix: offer AES-128/256-CBC (and AES-GCM only if your partners support it), use SHA-256 explicitly (`CmsSigner.DigestAlgorithm`) with `micalg="sha-256"`, keep 3DES behind an explicit opt-in for legacy partners.
   Make `EncryptionAlgorithm` `const` (it is mutable `public static string` today, so anything can change it at runtime).
7. **Content parsing assumes well-formed input** (`ExtractPayload`, `GetBoundaryFromContentType`, `Trim`, `Process`). `IndexOf` results of `-1` are used in `Substring`, so malformed input throws `ArgumentOutOfRangeException` or returns garbage.
   `ContentType.Contains` throws a `NullReferenceException` if the header is missing. Replace the ad-hoc string parsing with a real MIME parser (for example MimeKit) or at least bounds-checked parsing, and return a proper 400 rather than a 500.
8. **Line-ending bug**: `MESSAGE_SEPARATOR` is `"\r\n\r\n"` but the writer uses `Environment.NewLine`, which is `"\n"` on Linux. Messages built on Linux can't be parsed by this code and violate the RFC (CRLF required). Use `"\r\n"` constants everywhere.
9. **`GetMessage` and `BadRequest` write garbage.** They call `httpResponseMessage.GetBytes()`, which is the generic JSON+deflate extension from `StreamDataExtensions`, so it serializes an `HttpResponseMessage` object graph (and compresses it) to the response instead of the HTML.
   Both should write the HTML (or just the status) directly. This is a side effect of D-9 (the unconstrained `GetBytes<T>` extension).
10. **`AS2Send.SendFile` weaknesses**:
    - `Message-Id` is `"<AS2_" + DateTime.Now.ToString("hhmmssddd") + ">"`: 12-hour clock, no date, no milliseconds. It will collide, and partners that de-duplicate will drop real messages. Use `"<" + Guid.NewGuid().ToString("N") + "@" + hostname + ">"`.
    - `AllowAutoRedirect = true` on a POST of signed/encrypted business data can send it, with credentials, to another host. Set to false.
    - Response handling: only the status code is read. There is no MDN parse/verify, so a partner's rejection or an unsigned receipt is treated as success. Non-2xx throws, but the response is not disposed.
    - Blocking sync I/O, no `CancellationToken`, and `timeoutMs` applies only partially.
    - Header values built from `filename`, `from` and `to` are not validated (CR/LF, quotes in `Content-Disposition`).
    - `UserAgent = "MY SENDING AGENT"`. Make it configurable.
    - Argument checks are wrong (`new ArgumentNullException("filename")` for an empty string, `fileData` null unchecked, and an unreachable `throw new ArgumentNullException(recipientCertFilename, ...)` inside `if (encrypt)`).
11. **`A2Listener.ProcessRequest` logic is unsafe.** `GET` with a non-empty query string is treated as a data submission. `context.QueryString[0].Length` can throw when empty or null. No size limit, no HTTPS requirement, no authentication hook.
    `IHttpContext` wraps `WebRequest`/`WebResponse` (client types), so it can't be plugged into ASP.NET Core in practice. Redesign as ASP.NET Core minimal-API / middleware (`HttpContext`) or a `Func<HttpRequest, ...>` handler.

### C-4. Medium

12. Certificate and secret handling. Passwords are plain `string`s, and `ProxySettings` is a public mutable struct with a `Password` field. Load certificates through an options class, support `X509Certificate2` objects and a store lookup, and keep secrets out of logs. Dispose certificates (they hold native handles). Log-and-throw nothing from them.
13. Payload is converted through `Encoding.ASCII`, which corrupts any non-ASCII or binary payload (EDI is often fine, but XML/UTF-8 is not). Treat the payload as `byte[]` end to end.
14. `BadRequest` embeds `message` into HTML without encoding. It is only called with a constant today, but encode it (`WebUtility.HtmlEncode`) before someone passes input in.
15. No logging, no cancellation, no telemetry hooks. Add `ILogger`, and log AS2 message IDs, never payloads or secrets.
16. Replay protection and de-duplication are absent. Track `Message-ID` for a time window on the receiving side.

### C-5. Required tests before release
Round trips: sign then verify, encrypt then decrypt, sign+encrypt then decrypt+verify, using test certificates generated in the test (`CertificateRequest`).
Negative tests: wrong cert, tampered body, path-traversal `Subject`, oversized or negative `Content-Length`, missing or malformed `Content-Type` and boundary, duplicate `Message-ID`, partial reads, and a CRLF/LF matrix.

---

## D. Stability and correctness (core library)

### High

- **D-1. The queue is LIFO and drops the wrong item** (`DataStream`). `ConcurrentStack` is used as the queue. `NextResult()` returns the newest item, so consumers see data in reverse order.
  When the queue is full, `PushData` calls `TryPop`, which removes the **newest** item (not the oldest). The documented purpose is buffering a stream, so FIFO with drop-oldest is expected.
  Fix: use `ConcurrentQueue<StreamData>` (or a `Channel<T>` with `BoundedChannelFullMode.DropOldest`). Also make the count check and push atomic: the count and push race, so the bound can be exceeded by concurrent producers.
  This changes observable behavior, so cover it in the release notes.
- **D-2. An exception in a subscriber crashes the process.** `TimerElapsed` invokes user event handlers on a thread-pool thread. Since .NET Core 3.0, `System.Timers.Timer` no longer swallows exceptions thrown from `Elapsed`, so a throwing handler terminates the process.
  Fix: wrap invocation in try/catch (and report through an `Error` event or `ILogger`), or invoke each handler in the invocation list separately.
- **D-3. The timer callback is not thread-safe or re-entrant.** `_lastTime` (a `DateTime` field), `_stopwatch` and `random` are touched from overlapping callbacks (the timer can fire again before the previous handler returns). A shared `Random` instance is not documented as thread-safe.
  Fix: serialize the tick with a lock or `Interlocked`, or use `PeriodicTimer` with a single async loop (preferred on net10/net11), and use `Random.Shared`.
- **D-4. It emits fake data.** `TimerElapsed` generates `1.0345 * random.Next(0, 10000)`. The comment calls it a 'dataservice example'. A published library should not produce random values as "stream" data.
  Fix: introduce a data source abstraction (`IDataSource` / `Func<StreamData>`) and move the random generator into the tests or a sample. Decide with the owner whether this class is meant to be a demo. The README says "demonstrates how to use `DataStream`".

### Medium

- **D-5. `Reliability` can go negative.** `(100 - |interval - elapsed|) / 100` is below zero when a tick is more than 100 ms late (common under load). The stopwatch is also only started for `StreamType.Time`, and the first tick uses an unstarted stopwatch. Clamp to `[0,1]` and define the formula in the docs.
- **D-6. Hard-coded 1000 ms** in `TimerElapsed` ignores `PollingInterval`. Any `IntervalInMilliseconds` below 1000 silently drops ticks, and a larger one works only by accident. Use the configured interval.
- **D-7. `NextResult()` returns `default(StreamData)` when empty.** The struct's `Crc` is null, so callers get a `NullReferenceException` later and cannot tell "no data" from data. Add `bool TryNextResult(out StreamData data)` and obsolete the old one (or make `Crc` non-null).
  Also the `TryPop` result is ignored (race between `IsEmpty` and `TryPop`).
- **D-8. Dispose and lifecycle.** After `Dispose()`, `Start()`/`Stop()` throw `ObjectDisposedException` from the timer, handlers are not cleared, and a tick running during dispose can still fire events. The `_timer != null` check is dead code. Add `ObjectDisposedException.ThrowIf`, clear the event handlers, and wait for in-flight ticks.
  `AutoStart` also starts the timer before the stopwatch and `Configured` are set up, so a very early tick sees a half-built object. Move `_timer.Start()` to the end of the constructor.
- **D-9. Serialization and the `StreamDataExtensions` surface.**
  - `GetBytes<T>(this T value)` and `Deserialize<T>(this byte[] data)` are unconstrained extension methods in a namespace consumers import, so **every** object and every `byte[]` gets them. This is how the A2 code ended up JSON-compressing an `HttpResponseMessage` (C-3 #9). Rename to `ToCompressedJson`/`FromCompressedJson` and, if possible, constrain to `StreamData`.
  - **Decompression bomb**: `Decompress` copies without any output limit. A few KB of attacker data inflates to gigabytes. Cap the decompressed size (for example `Stream.ReadAtMost` pattern with a configurable limit) and throw `InvalidDataException`.
  - Raw Deflate has no header or integrity check, so truncated or corrupted data fails late with obscure errors. Consider `ZLibStream`/`GZipStream` with a magic header and a version byte.
  - Reflection-based `JsonSerializer` is not trimming/AOT-safe. Use a `JsonSerializerContext` for `StreamData` and `StreamConfig` if AOT matters to consumers.
  - Streams are not disposed in `Compress`/`Decompress` (harmless for `MemoryStream`, but use `using`).
- **D-10. `DataStreamManager` state is inconsistent and not thread-safe.**
  - `StreamConfig` (a public `List`) and the internal `_streams` dictionary only sync in `Initialize()`. The existing test adds to `StreamConfig` and then calls `SaveConfig`, but `ListClients()`/`CreateClient()` will never see that entry. Make `_streams` derived from `StreamConfig`, or make the list read-only with `Add/Remove` methods.
  - `Initialize` deserializes into a `DataStreamManager` instance, so a config file can set `Configfile` and `FileName`. That is surprising, and it allows a config file to redirect where the next load or save goes. Deserialize into a dedicated `DataStreamOptions`/DTO.
  - Duplicate stream names silently overwrite each other (`Remove` then `Add`). Throw or log. A null `Name` throws `ArgumentNullException` from the dictionary.
  - `Dictionary` and `List` are mutated by `Initialize` while `CreateClient` may read them: use a lock or immutable snapshot swap.
  - Wrap errors: `throw new System.Exception("Error reading config file")` loses the cause. Use `JsonException`/`InvalidDataException` with the inner exception and file name.
  - `SaveConfig(string streamName)`: the parameter is a **file path** (named "streamName"), written non-atomically and with different JSON options than `Initialize` reads (no enum converter, no `IncludeFields`). Write to a temp file and move it, use the same options for both, and rename the parameter.
- **D-11. `CreateClient` failure modes.** `CreateClient(string)` throws a bare `KeyNotFoundException`. `CreateClient(StreamType)` uses `FirstOrDefault` over a **struct** and silently constructs a `DataStream` from `default(StreamConfig)` when nothing matches (the existing test `FindStreamClient` depends on `Configured == false` for this). Decide on the intended contract:
  prefer `TryCreateClient` plus an exception with a clear message, and update the test. The first-match logic also returns an arbitrary entry when several configs match.

### Low / data model

- **D-12. `StreamType` is used as flags but is not `[Flags]`** (`Time | Position` = 3 is not a defined member, and it serializes as `"3"` while single values serialize as names). Add `[Flags]`.
- **D-13. CRC is not a real CRC and has a precision bug.**
  - `AddValue` keeps a growing `int` (`val * 2` overflows past 17 bits, and only bit 16 is checked), so this is neither CRC-16 nor any standard polynomial. It is also endianness-dependent via `BitConverter`. Nothing in the library ever verifies the CRC.
  - `Geo` casts before multiplying: `(long)value.Latitude * 1000000` truncates the fractional part first, so 52.37 and 52.99 hash the same. It should be `(long)(value.Latitude * 1000000)`. Latitude/longitude are also `float` (about 1 m precision), so consider `double`.
  - `Crc` is a mutable reference type inside a struct `StreamData`. Copies share it, and it is computed only in the constructor, so changing `Value` afterwards leaves a stale CRC. Make `StreamData` an immutable `readonly record struct` with a computed or verified `Crc`, and add `bool Verify()`.
  - Replacing the algorithm changes the wire format. Version it (`System.IO.Hashing.Crc32` is a good choice) and mention it in the 2.0.0 notes.
- **D-14. `Constants.Epoch` is a mutable `public static` field** with `DateTimeKind.Unspecified`. Make it `static readonly` with `DateTimeKind.Utc` (it is a `DateTime`, so it cannot be `const`). `StreamData.DateTime` mixes UTC (`UtcNow` in the stream) and the unspecified Epoch default. Use `DateTimeOffset` or enforce UTC. The CRC also hashes `Ticks` without Kind.
- **D-15. `Geoposition.ToString()` has N/S and E/W inverted.** `Latitude < 0 ? "N" : "S"` means the north hemisphere (positive latitude) prints as S, and the same for longitude. It should be `Latitude >= 0 ? "N" : "S"` and `Longitude >= 0 ? "E" : "W"` (and print `Math.Abs`).
- **D-16. Naming typos in public API**: `DefaultMaxiumQueueLength`, `OnDataAvailable(..., float responsibility)` (should be reliability), `Configfile` vs `FileName`, `A2Protocol` (AS2). Fix in the 2.0.0 break and add `[Obsolete]` forwards only if you want a soft transition.
- **D-17. Tests.**
  - `Thread.Sleep(6000)` and several `Sleep(200)` calls make the suite slow and timing-flaky on CI. Inject a `TimeProvider` (built in since .NET 8) and drive ticks deterministically.
  - There are no tests for the A2 code, for concurrency, for queue ordering/overflow, or for decompression limits.
  - Tests use Windows paths (`C:\Temp`), which is fine only because the file system is mocked. Use `MockFileSystem` from `System.IO.Abstractions.TestingHelpers` for real behavior.
  - Add `net10.0` and `net11.0` to the matrix and add a coverage gate.

---

## E. Suggested work breakdown (PR-sized)

1. **PR 1 (build and packages).** A-1..A-3, B-1..B-6. Land first so CI is trustworthy and the audit is on. Version 2.0.0-preview.1 (publish **only** via tag).
2. **PR 2 (compile fixes on net10/net11).** C-1 minimum: replace obsolete APIs, fix nullability. Get green.
3. **PR 3 (core stability).** D-1..D-11 with tests (FIFO queue, thread safety, exception isolation, `TimeProvider`).
4. **PR 4 (AS2 security).** C-2 and C-3 with the C-5 test suite. This is the largest PR. Consider splitting send and receive.
5. **PR 5 (data model).** D-12..D-16 (CRC, `StreamType`, `Geoposition`, naming). Breaking, but it is already a major release.
6. **Release.** Tag `v2.0.0`, README migration notes (dropped netstandard2.0/net8.0, behavior changes in D-1/D-7/D-11, CRC format change).

## F. Questions still open for the owner

- Is `DataStream` meant to be a demo (random data), or a real timer-based data source? This decides D-4.
- Is any external consumer relying on the current CRC value or the LIFO order (D-1, D-13)? Those are wire/behavior breaks.
- Which AS2 partners must be supported (3DES-only legacy partners, AES-capable)? This decides the C-3 #6 defaults.
- Should `A2Protocol` be renamed to `As2` in 2.0.0?
- Is .NET 11 GA on the runners' `setup-dotnet` yet? If not, allow preview for the 11 job and document it.
