# Handover: N2.DataStreams

Current state (2026-11-24): version 3.0.1, part of the "N2 unify 3.0.1" operation. The older
review/remediation plan that lived in this file was superseded; see git history for it.

## State

- Package: `N2.DataStreams` 3.0.1 (AssemblyVersion 3.0.0.0, FileVersion 3.0.1.0).
- Target frameworks: `net10.0;net11.0` (library and unit tests).
- netstandard2.0 was evaluated and left out. The code needs many APIs that do not exist there
  (ThrowIfNull, Encoding.Latin1, Math.Clamp, SocketsHttpHandler, X509Certificate2.RawDataMemory,
  Convert.TryFromBase64String, File.Move overwrite, and others), and the public API uses
  `required`/`init` members and `TimeProvider`. It would need many source changes, including to
  the AS2 transport, so it was not pursued.
- Dependencies: System.IO.Abstractions 23.1.0; System.Security.Cryptography.Pkcs per TFM
  (10.0.15 on net10.0, 11.0.0 on net11.0).
- Tests: 95 unit tests (MSTest), all passing on both TFMs.

## Source and publishing

- GitHub (`Kaalenco/n2-data-streams`, branch `trunk`) is the source of truth.
- `.github/workflows/dotnet.yml`: build and test on push and pull request to trunk. A push to
  trunk also publishes `N2.DataStreams` to the private `vault-packages` feed with
  `--skip-duplicate` (so only a version bump publishes). Feed config: `src/nuget.config`;
  secret `NUGET_PAT`.
- The workflow never pushes to nuget.org. nuget.org is promoted from vault-packages by the
  `n2-publishing` repository. Currently on nuget.org: 1.0.0 to 1.0.2.

## Open items

- Decide whether the `A2Protocol` namespace (the protocol is AS2, RFC 4130) is renamed in a
  later major version.
- AS2 hardening and stability items from the earlier review are not tracked here any more;
  recover them from git history (`git log -- handover.md`) if needed.
- Revisit netstandard2.0 only if a consumer needs it.
