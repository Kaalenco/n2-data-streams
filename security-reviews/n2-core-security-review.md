# Security review: N2.Core

Review date: 2026-11-21. Reviewed: default branch of `Kaalenco/n2-core` (`src/N2.Core`, csproj `1.6.3`, targets `netstandard2.0;netstandard2.1;net8.0;net10.0`), with `N2.Core.Abstractions` read for context (see its own `security-review.md`).
Note: nuget.org already lists `N2.Core` **2.0.0** and `N2.Core.Abstractions` **2.0.0**, which are newer than this branch (1.6.3 / 1.7.0). If 2.0.0 was built from another branch, re-check these findings against it first.
Method: manual read of the security-relevant source (identity/JWT/OAuth, command pipeline, HTTP helpers, configuration, DI scanning, background work, localisation) plus the workflow and csproj. Nothing was built or run, and there was no SCA tooling (no .NET SDK in the review environment). The unit tests were only grepped, not executed.
Severity: **High** (exploitable or secret-exposing by default), **Medium** (exploitable under common usage), **Low** (hardening).

## Summary

N2.Core is a general-purpose library, but it contains an **OAuth-style token endpoint handler** (`OAuthCommandHandler`), JWT helpers and a TOTP-like client-credential scheme. That is where the risk concentrates.
There are three top issues:
1. Credentials in command requests (`TokenRequest` with passwords, client secrets and refresh tokens) are serialised into logs and, in one path, into the response to the caller.
2. The `client_credentials` handler appears to pass its arguments to `ValidateTOTP` in the wrong order, and the scheme has weaknesses (replay, base64 parsing exceptions, thread blocking) that make it unsuitable as written.
3. Unbounded thread creation in `Conductor` and synchronous waits in the command handler give an easy denial-of-service path.

| ID | Sev | Area | Title |
|---|---|---|---|
| C-1 | High | Logging | Secrets in command requests are serialised to logs (`BaseCommandHandler`, `Conductor`) |
| C-2 | High | Disclosure | `WaitForAsync` returns the serialised request to the caller |
| C-3 | Medium | Disclosure | `ex.Message` and unsupported grant text returned to clients, and `try/catch` does not catch async failures |
| C-4 | High | Auth | `client_credentials`: `ValidateTOTP` arguments look swapped, and the one-time hash is passed on as the secret |
| C-5 | Medium | Auth | TOTP design: no replay cache, minute truncation, effective window is 3 min, non-standard construction |
| C-6 | Medium | DoS | `ValidateTOTP` blocks a thread 500 ms per call, and attacker-controlled base64 throws before the delay |
| C-7 | Medium | Auth | Refresh token is a plain `Guid`, and its registration is fire-and-forget with a 200 ms race |
| C-8 | Medium | JWT | Token validation parameters: optional issuer/audience, no algorithm pinning, no key length check, clock skew |
| C-9 | Medium | JWT | `GetPrincipal` throws `IndexOutOfRangeException` on `"Bearer"` with no token, and returns `null!` |
| C-10 | Medium | Config | Settings precedence and the `ENV__` prefix handling look inverted/incorrect, so env vars may not override files |
| C-11 | Low | Config | Hard-coded `UserSecretsId` in a published library, and user secrets loaded in every environment |
| C-12 | Medium | DoS | `Conductor` starts an unbounded number of threads, silently drops commands, and the custom `Semaphore` is unsafe |
| C-13 | Medium | DoS | `BaseCommandHandler` sync-over-async (`Task.Wait`) and no cancellation: thread starvation, work continues after timeout |
| C-14 | Medium | Input | `AzureFunctionClient`: path handling, no HTTPS enforcement for the function key, response not disposed/bounded |
| C-15 | Low | Input | `FromQueryString<T>` reflection setter: type confusion exceptions and no length limits |
| C-16 | Low | Input | `HttpRequestExtensions`: `Host` and `AuditTag` header reflected without validation |
| C-17 | Low | Input | `LocalizedTextService.Translate`: unvalidated culture name throws, and the culture fallback ignores the requested culture |
| C-18 | Low | Crypto | `RandomStringGenerator`: wrong `#if` symbol selects the weaker path on modern TFMs, and the "not for security" contract is easy to violate |
| C-19 | Low | Misc | `OAuthConfig`: unchecked numeric settings (division by zero, negative or huge lifetimes), no secret strength check |
| C-20 | Low | DI | Assembly scanning registration (`AddInterfacesOfType`) |
| C-21 | Low | Telemetry | `ActivityLog.TagError` exports full stack traces and messages to the trace backend |
| C-22 | Medium | Pipeline/Supply chain | Workflow: PR-triggered publish, third-party secrets action, outdated SDKs vs `net10.0`, unpinned actions |
| C-23 | Low | Dependencies | Package status and legacy `Microsoft.AspNetCore.Http 2.3.x` |

---

## Findings

### C-1 (High): secrets are logged
- `BaseCommandHandler.Validate` logs `JsonSerializer.Serialize(new { Request = request, ... })` when validation fails. For `TokenRequest`, `request.Value` is a `Token`, which carries `ClientSecret`, `AccessToken` (the Base64 `user:password` for the `basic` grant!) and `RefreshToken`. A single invalid login attempt writes the user's password (Base64) to the warning log.
- `Conductor.Invoke`, `.CallBack` and the thread body log `JsonSerializer.Serialize(command)` / `Serialize(result)` on handler failure and for "not found" cases. This also covers responses that contain issued `AccessToken` and `RefreshToken` values.
- `BaseCommandHandler.LogExecutionError` logs the indented serialised request.

**Fix.**
1. Never serialise whole requests/responses in logs. Log the type name, the tracking id and a few **allow-listed** fields.
2. Make secret-bearing members opt out of serialisation: `[JsonIgnore]` on write for `ClientSecret`/`AccessToken`/`RefreshToken` in a log-only serializer, or a `SensitiveData` marker and a redacting `JsonTypeInfoResolver` modifier for logging. Add a unit test that logs a failing `TokenRequest` and asserts no secret substring appears in the captured logs.
3. Treat existing logs as compromised for any environment that ran these paths. Rotate credentials that may have been logged.

### C-2 (High): request echoed to the caller
`BaseCommandHandler.WaitForAsync` builds `Command not accepted: {status}\n {JsonSerializer.Serialize(request, options)}` and returns it in `Message`. If this message reaches an HTTP client (and `CommandResponse.Message` is the obvious thing to return), the caller gets their own request back, plus anything the server added to it (and for handlers that enrich requests, other users' data). With the logging issue above, this is the same defect on the response channel.
**Fix.** Return a fixed message (`"Validation failed for the request"`, as `HandleRequest` already does). Put details in server-side logs keyed by the tracking id.

### C-3 (Medium): exception text and async error handling
- `WaitForAsync` wraps `return HandleRequestAsync(request);` in `try/catch`, but the task is **returned, not awaited**, so exceptions raised inside the async handler (e.g. `UnauthorizedException` from `OAuthCommandHandler`) bypass the catch and surface to the caller, while the catch only sees synchronous throws. When the catch does fire, `ex.Message` is returned to the client (`ResponseStatus.ServerError, ex.Message`), which can contain SQL text, paths or connection details.
- `OAuthCommandHandler` echoes the client-supplied grant type in its failure message (`authorization schema not supported: {token.GrantType}`). That is reflected input (log/HTML injection if rendered).
- Unsupported-grant, bad-credential and "no access" responses differ in text and type (`UnauthorizedException("No Accesstoken")` for several different faults, `"No Access"` when the user has no secret, `Guid.Empty` for a bad TOTP), which permits user/client enumeration.

**Fix.** `await` inside the `try`, return a generic message plus tracking id, log the exception server-side, and make all credential failures indistinguishable (same status, same body, similar timing).

### C-4 (High): `client_credentials` argument order and secret handling
`OAuthCommandHandler.HandleClientCredentials` calls
`JwtTools.ValidateTOTP(DateTime.UtcNow, secret /* stored */, token.ClientSecret /* from client */, window)`,
but the signature is `ValidateTOTP(DateTime referenceTime, string hash, string secret, int window)` (and the unit tests call it with `(hash, secret)`).
With the call as written, the **client-supplied value is used as the HMAC key** and the **stored secret is compared as if it were the hash**. At best this fails closed for every legitimate client, so the handler is dead or untested. At worst, if `GetUserSecret` returns something derivable by the client, it changes the trust model. There are no tests of this handler.
Then, on success, the handler calls `LogonUserWithSecret(ClientId, ClientSecret)`, passing the **one-time hash** (not a long-term secret) to the identity manager, which makes it easy for an `IIdentityManager` implementation to compare it against the stored permanent secret, or to store/log it.

**Fix.**
1. Confirm the intended contract, swap the arguments and add handler-level tests for success, wrong hash, expired hash, unknown client, and each missing field.
2. Replace this custom scheme with a standard one (see C-5), or with the OAuth `client_credentials` grant over TLS with a hashed (Argon2id/PBKDF2) client secret.
3. Define `IIdentityManager.LogonUserWithSecret` so it never receives raw one-time values, and never stores or logs them.

### C-5 (Medium): TOTP-like construction
`JwtTools.TOTP/ValidateTOTP` is custom:
- It builds an HMAC-SHA256 over the decimal string of `epoch`, truncates the reference time **to the minute** and then divides by `timeWindowInSeconds`. For windows under 60 s the setting has no effect, because steps are minute-aligned. Because `epoch-1`, `epoch` and `epoch+1` are all accepted, a hash stays valid for roughly three steps (about 3 minutes at the default window, and about 15 minutes at the 300 s used in the tests).
- The full 256-bit MAC is the token. That is long for a human, and fine for a machine, but it is not RFC 6238.
- There is **no replay protection**. A captured hash is reusable throughout its window (the configuration property is called `ReplayWindowInSeconds`, but it actually widens the replay window).
- Base64 inputs from the secret/hash are parsed with `Convert.FromBase64String` (throws `FormatException` on malformed values, see C-6).

**Fix.** Use RFC 6238 (HOTP/TOTP with SHA-256, 6-8 digits for humans) for MFA, or sign a short-lived JWT/assertion (RFC 7523) for service clients. Keep a server-side "used counter" cache per client so each value is accepted once. Do not truncate the timestamp. Document the clock-skew budget.

### C-6 (Medium): CPU/thread DoS in `ValidateTOTP`
- Every call sleeps with `Thread.Sleep` until 500 ms have elapsed. A caller who sends a stream of requests ties up one thread-pool thread per request for 500 ms, so a few hundred requests per second starve the pool.
- The padding only applies after a successful HMAC computation. Input that is not valid base64 throws `FormatException` immediately (no delay), which gives an unauthenticated timing oracle for "well-formed vs not", and an unhandled exception path.
**Fix.** Do not sleep. Use rate limiting per client/IP (ASP.NET `RateLimiter`) and uniform work, with `CryptographicOperations.FixedTimeEquals`. Catch base64/length errors and return `false` (after the same comparison work).

### C-7 (Medium): refresh tokens
- `Guid.NewGuid()` is used as the refresh token. On .NET Core+ it is random (122 bits, from the OS CSPRNG), but the API contract does not promise unpredictability, and on older runtimes it varies. Use 32 random bytes (`RandomNumberGenerator.GetBytes(32)`), Base64Url-encoded. Store only a hash of it server-side (SHA-256).
- `CreateValidJwt` starts `RegisterRefreshToken` **without awaiting** it and races it against a 200 ms delay. If it is slow, the token is registered **later** but the caller receives `Guid.Empty`. If it **faults quickly**, `Task.WhenAny` returns the (faulted) task itself, so the code thinks it succeeded, returns the refresh token that was never stored, and the exception is never observed.
- `GetClaims` is awaited concurrently with the registration, so the JWT may be minted for a user whose registration failed.
- The doc says "the refresh token can be used only once", but rotation, reuse detection (revoke the whole family on reuse) and expiry bounds live entirely in `IIdentityManager`, with no contract for them.
- `HandleLogoff` defaults the scope to `"*"` (log off everywhere) and accepts any refresh token GUID without caller authentication, which is only safe if the refresh token is a strong bearer secret (see above).

**Fix.** `await` registration and fail the token request if it fails. Use strong opaque tokens, hashed at rest, with rotation and reuse detection. Define these obligations on `IIdentityManager`.

### C-8 (Medium): JWT validation settings
`GetPrincipalFromJwt` builds `TokenValidationParameters` with:
- `ValidateIssuer = issuer != null`, `ValidateAudience = audience != null`. If a caller omits them (they are optional parameters defaulting to `null`) any token signed with the same key is accepted for any audience, which enables cross-service token reuse. Make issuer and audience **required**.
- No `ValidAlgorithms`. `SymmetricSecurityKey` will accept HS256/384/512. Pin `ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 }`.
- No key-size check. A short key (`OAuthConfig.Secret` is any Base64 string) is accepted by this code until IdentityModel throws at sign time. Reject keys under 32 bytes at startup.
- No explicit `ClockSkew`, so the default is 5 minutes. With `FiveMinutes`/20 minute lifetimes that is significant, so set it to 30-60 s.
- No `RequireSignedTokens = true` (it is the default, but state it), `ValidateLifetime` (default true, state it), and `ValidateIssuerSigningKey = true`.
- Single static key, with no `kid` and no rotation support. Add `IssuerSigningKeys` (current + previous).
- `JwtSecurityTokenHandler` is the legacy handler. IdentityModel recommends `JsonWebTokenHandler`, which is faster and has better validation defaults.
- `ConvertToJwt` also emits `notBefore = DateTime.UtcNow` (fine) and lets the caller choose any `tokenTimeout`, including `JwtTokenTimeout.OneYear` as a bearer lifetime. Cap lifetimes.
- Failure wraps the underlying exception in `WebTokenException("JWT Token validation failed", e)`. The inner exception message (for example "Lifetime validation failed. The token expired at ...") must not be sent to clients.

### C-9 (Medium): `GetPrincipal` edge cases
`authorization.Split(' ')` followed by `bearer[1]` throws `IndexOutOfRangeException` for the header value `Bearer` (no token) and `Bearer ` variants with multiple spaces mis-split. A missing/unsupported scheme returns an unauthenticated principal, but a bad token **throws**. `GetPrincipalFromJwt` can return `null!`. Callers that don't expect either will 500 or NRE. Use `AuthenticationHeaderValue.TryParse`, return a consistent "anonymous" result and never throw on malformed input.

### C-10 (Medium): configuration precedence and key mapping
In `SettingsService.LoadConfiguration` the order is: environment variables (as an `AddInMemoryCollection`, added **first**), then `appSettings.json`, then user secrets. Later providers win, so a value in the JSON file **overrides** the deployment's environment variable, the opposite of the usual .NET convention, and the opposite of what you want for secrets and hardening flags. A committed `appSettings.json` default beats a production override.
Also `key.Replace("__", ":")` is applied to the whole key, including the prefix, so `ENV__OAuthConfig__Secret` becomes `ENV:OAuthConfig:Secret`, which `OAuthConfig` (`configuration["OAuthConfig:Secret"]`) would not read. Please add a test with an env var through `SettingsService` to verify. Whichever way it behaves, the contract is surprising.
`EntityConnectionService` has no environment-variable support at all, so connection strings (with credentials) can only come from a file or user secrets.
**Fix.** Use the standard builder: `AddJsonFile` → `AddUserSecrets` (Development only) → `AddEnvironmentVariables(prefix)`, so the environment wins. Strip only the prefix.

### C-11 (Low): user secrets
`<UserSecretsId>N2-Core-0c368d89-...</UserSecretsId>` is baked into the published assembly, and `AddUserSecrets<EntityConnectionService/SettingsService>()` runs in every environment. Any process using the library reads `~/.microsoft/usersecrets/N2-Core-.../secrets.json` if the file exists. An attacker who can write that file (shared build agent, developer machine) injects configuration (connection string, OAuth secret) into **all** consumers. Load user secrets only when `IsDevelopment()` and from the **consumer's** assembly id, never the library's. Remove the `UserSecretsId` from the library csproj.

### C-12 (Medium): `Conductor` thread model and `Semaphore`
- `Invoke` starts a **new dedicated `Thread` per handler per command** with no cap, before the semaphore check. Many commands, each starting a thread that waits `ThreadTimeOut` (min 10 ms), can exhaust memory/threads.
- If the (non-OS) `Semaphore` limit of 20 is reached, the handler is **silently dropped**: the caller still receives `ResponseStatus.Accepted`. For commands such as "revoke token", "lock account" or "write audit entry" that is a correctness and security problem (lost security events).
- Handler exceptions are swallowed after logging (with the full command, see C-1).
- `N2.Core.Semaphore` is a hand-rolled spin/sleep limiter: `Release()` has no lower bound (extra releases drive `CurrentValue` negative, raising the real concurrency above the limit), `Name` is decorative, and `WaitOne` polls with `Thread.Sleep(1)`.
- `RegisterCallback` creates a new random `Guid` that is unrelated to any command handle, so callbacks cannot be matched to a command and may never fire; entries leak unless the registration is disposed.
**Fix.** Use `Channel<T>` with a bounded capacity and a fixed set of worker tasks (or `SemaphoreSlim` with `WaitAsync`), return a "busy" status (429/503) when full instead of `Accepted`, and persist security-relevant commands before acknowledging them.

### C-13 (Medium): sync-over-async and cancellation
`BaseCommandHandler.HandleRequest` calls `HandleRequestAsync(request)` and then `task.Wait(timeoutMs)`. This blocks a thread per request (with `TimeoutInMilliSeconds` default 200 ms, so each call stalls for up to 200 ms, or 10 minutes when the property is set to ≤ 0, through `LongTimeout`). In an ASP.NET context with a synchronization-sensitive handler this can deadlock, and under load it starves the pool. As the in-code comment notes, the task **keeps running after the timeout**, so a client that receives `TimeOut` may still have caused the side effect, and can retry it (duplicate writes, double charges). Also handler state (`_request`, `_result`, `response`) lives in instance fields on handlers registered as scoped services (see C-20), so a handler instance reused across calls or threads leaks one request's result to another.
**Fix.** Make the API async end to end with a `CancellationToken` passed into `HandleRequestAsync`, remove the instance-level request/result state, and add an idempotency key for retried commands.

### C-14 (Medium): `AzureFunctionClient`
- `new Uri(basePath, $"/api/{functionPath}")`: `functionPath` is unvalidated, so `../admin` or `x?y=z#` change the target path/query, and an absolute-path reference like `/api/` discards any path in `basePath` (e.g. `https://host/app/`). The host stays fixed (limited SSRF), but a caller that passes user input can reach other functions on the same app and **send them the function key**. Validate against an allow-list or `[A-Za-z0-9_/-]+` and URL-encode segments.
- The constructor accepts any `Uri` and the key is sent as `x-functions-key` on every call. Require `https` (or loopback) and fail otherwise, otherwise the key is exposed on the network.
- The `HttpResponseMessage` is never disposed, the response is read fully into a string with no size limit, and the same `JsonSerializerOptions` object is cast from `IDefaultValueService.JsonSerializerOptions` (invalid cast if it isn't a `JsonSerializerOptions`).
- On failure it logs the URL and status code via `Tag("Error", ...)`. This is fine, but make sure URLs never contain the key or tokens in the query.
- The API key is held in a plain `string` field. Prefer `IOptions`/Key Vault references and avoid logging the client object.

### C-15 (Low): `FromQueryString<T>`
For each public property whose name matches a query key, it calls `property.SetValue(result, value[0])` with a `string`. For any non-`string` property this throws `ArgumentException` (HTTP 500, an easy unauthenticated error generator). It also binds **every** settable property by name, including ones that were not meant to be user-controlled (mass assignment) and has no length limit. Use model binding (`[FromQuery]`) or convert with `TypeDescriptor`/`Convert.ChangeType` against an allow-list with length caps.

### C-16 (Low): HTTP helpers
- `GetEncodedUrl`/`BuildAbsolute` build the reference URI from the incoming `Host` header and put it in `HttpResult.ReferenceUri`. Behind a proxy that doesn't validate `Host`, this reflects an attacker-controlled host (host-header injection / poisoned links). Use `AllowedHosts` and `ForwardedHeaders` configuration, and prefer a configured public base URL.
- `HttpHeaderType.AuditTag` is copied from the client request into results/logs without length or character validation (log forging, CR/LF in some sinks). Validate to a safe charset and length (e.g. 64 chars of `[A-Za-z0-9-_.]`), or generate it server-side.
- `HttpRequestProxy.Headers` flattens multi-value headers with `StringValues.ToString()` (comma-joined) and `ReadAsStringAsync` is `NotImplementedException`, which will 500 if called.
- `HttpRequestProxy` exposes **setters** for `ContentType`, `ContentLength` and `Body`, which allows handlers to mutate the real request.

### C-17 (Low): `LocalizedTextService`
- `Translate(language, ...)` uses `new CultureInfo(language)` on caller-supplied input: invalid names throw `CultureNotFoundException` (500), and the cache key `"{culture.Name}:{key}"` grows with every distinct key. Resource keys are caller-controlled in some usages, so cap the cache (`MemoryCacheOptions.SizeLimit` and entry `Size`) and validate culture names against `CultureInfo.GetCultures`.
- The fallback path in the context overload uses `CurrentCulture` rather than the requested `culture`, so a request in one language can render in another (correctness, but it also produces cross-user language leakage when the service instance is shared, because `CurrentCulture` is a **mutable property** on a service that may be a singleton). Make the service stateless and pass the culture per call.
- Output is not HTML-encoded (and should not be) but resource strings are rendered by callers. Document that placeholders must be encoded at render time.

### C-18 (Low): `RandomStringGenerator`
- The guard is `#if NETSTANDARD2_1_OR_GREATER`. That symbol is **not defined** for the `net8.0`/`net10.0` targets, so modern builds compile the hand-written `GetInt32` fallback: a new `RandomNumberGenerator` per character, and a `% maxValue` reduction with a tiny modulo bias, instead of `RandomNumberGenerator.GetInt32`. Use `#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER`.
- Its XML doc says "do not use for security purposes", but it uses a CSPRNG and the alphabet (57 chars, 5.8 bits per char) is easy to mistake for a token generator. Either supply a proper `GenerateToken(bytes)` API (Base64Url) or document the entropy per length and state when it is acceptable.
- `RandomNames` reuses the internal `GetInt32` and is fine.

### C-19 (Low): `OAuthConfig`
- `ReplayWindowInSeconds = 0` leads to `DivideByZeroException` in `ValidateTOTP`; negative or very large values are accepted. `TokenTimeoutInMinutes` of 0 or negative or years is accepted. Validate ranges at startup (e.g. 1-60 min for access tokens).
- `Secret` is a plain `string?`, not validated for length/entropy, and the handler decodes it with `Convert.FromBase64String` (throws on bad input at construction time with a confusing error).
- Use `IOptions<OAuthConfig>` with `ValidateOnStart()` and data annotations, and fail fast with an error that does not echo the secret.

### C-20 (Low): DI scanning
`AddInterfacesOfType<T>` scans `assembly.GetTypes()` and registers every non-abstract, non-nested class implementing `ICommandHandler`/`IRuntimeValidator` as **scoped**. Anything added to the scanned assembly (including a malicious or transitive dependency if the caller passes `typeof(X).Assembly` loosely) becomes an active command handler. `GetTypes()` also throws `ReflectionTypeLoadException` for partially loadable assemblies. Make the assemblies explicit and reviewed, use `GetExportedTypes` with a try/catch for `ReflectionTypeLoadException`, and log what was registered. Also `reference.Name != nameof(T)` compares against the literal `"T"` and does nothing.

### C-21 (Low): telemetry
`ActivityLog.TagError` adds `ex.Message` and `ex.StackTrace` as event tags. These are exported to whichever OpenTelemetry backend is configured and exception messages commonly contain connection strings, SQL and user data. Redact or truncate, and use the standard `exception.*` semantic-convention attributes with sampling. `Tag(string, object?)` accepts arbitrary values: do not tag secrets, and add a deny-list for keys such as `password`, `token`, `secret`.

### C-22 (Medium): CI/publish (`.github/workflows/dotnet.yml`)
- The **Publish step has no condition**, so it runs on `pull_request` as well as on `push`. A same-repo PR build tries to push to nuget.org with the real API key (it fails or publishes depending on the version). There is no `--skip-duplicate`.
- `Firenza/secrets-to-env@v1.3.0` gets `toJSON(secrets)` (all secrets) and exports them to the environment of every later step (build, tests, any code in the repo or its dependencies). Remove it.
- `setup-dotnet` installs SDK **8.0.x and 9.0.x only**, but the library and tests target `net10.0`. Either the build is failing/using a preinstalled SDK or it relies on runner defaults. Install `10.0.x` (and `11.0.x` when you add it).
- Actions are pinned to tags only, no `permissions:` block (default token scope can be read-write), no `concurrency`, and the build is Debug with `GeneratePackageOnBuild`, so the published package is a Debug build.
- There is no CodeQL/dependency review workflow in this repo (the abstractions repo has CodeQL), no Dependabot configuration, and `NuGetAudit` is on in the csproj but warnings from it are not errors.

### C-23 (Low): dependencies (checked against nuget.org on 2026-11-21)
| Package | In repo | Latest stable |
|---|---|---|
| System.IdentityModel.Tokens.Jwt | 8.17.0 | 8.23.0 (also consider moving to `Microsoft.IdentityModel.JsonWebTokens`) |
| Microsoft.AspNetCore.Http | 2.3.9 | 2.3.14 (legacy ASP.NET Core 2.x line, kept alive only for netstandard compatibility. Prefer `Microsoft.AspNetCore.Http.Abstractions` via `FrameworkReference` on net8+) |
| Microsoft.Extensions.* | 10.0.5 | 11.0.0 |
| System.IO.Abstractions | 22.1.0 | 23.0.1 |
| N2.Core.Abstractions | 1.6.3 | 2.0.0 |

I could not run `dotnet list package --vulnerable` here. Run it, and turn `NU1901`-`NU1904` into errors so the build fails on a vulnerable dependency.
Dropping `netstandard2.0` would remove the oldest, most exposed `Microsoft.AspNetCore.Http 2.x` and `System.Net.*` dependency chain.

---

## Things done well (keep)
- `JwtTools` uses fixed-time comparison for hash checks and `RandomNumberGenerator` for key generation.
- `RandomStringGenerator` uses a CSPRNG and an unambiguous alphabet.
- `Contract.ValidName` restricts names to a conservative character set and length.
- `FileHelpers` uses SHA-384 (not MD5/SHA-1) and `SanitizeFileName` removes path separators and dots (so `..` cannot survive). Add a length limit and reserved-name handling (`CON`, `NUL`) if its output is ever used as a path.
- Registration, logging and validation use `LoggerMessage` source generators, and analyzers are on (`AnalysisMode=All`, warnings as errors).
- `InMemoryChangeService` uses copy-on-write with a lock, and isolates listener exceptions.

## Suggested order of work
1. **C-1, C-2, C-3**: stop logging and echoing secrets (same day). Rotate credentials that may have been logged.
2. **C-4, C-5, C-6**: fix or disable the `client_credentials` grant until it has tests and a standard construction. Until then, remove `client_credentials` from the handler dictionary.
3. **C-7, C-8, C-9, C-19**: token issuance hardening (strong refresh tokens, awaited registration, required issuer/audience, algorithm pinning, key length checks, startup validation).
4. **C-22**: fix the pipeline (no publish on PR, remove the secrets action, SDK 10/11, permissions, SHA pins).
5. **C-10, C-11**: configuration precedence and user-secrets scope.
6. **C-12, C-13**: replace the thread-per-command and sync-over-async model (channel + async handlers + cancellation + idempotency).
7. Remaining Low items, then re-run an SCA scan and add regression tests for each fixed item (secret-in-log test, grant matrix, JWT negative tests, config precedence test).
