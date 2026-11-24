# Changelog

## 3.0.1 - 2026-11-24

Version unification: all N2 packages move to 3.0.1.

- Version: N2.DataStreams jumps from 2.0.0 to 3.0.1 (AssemblyVersion 3.0.0.0, FileVersion 3.0.1.0).
  nuget.org currently holds 1.0.0 to 1.0.2 only; 2.0.0 was never published there.
- Target frameworks: `net10.0` and `net11.0`. `netstandard2.0` was evaluated and left out: the
  public API uses `TimeProvider`, `required` and `init` members and System.Text.Json, which would
  need polyfills and extra packages.
- Dependencies: System.IO.Abstractions 23.0.1 -> 23.1.0 (also in the tests);
  System.Security.Cryptography.Pkcs per framework (10.0.15 on net10.0, 11.0.0 on net11.0).
- Publishing: the workflow no longer pushes to nuget.org on `v*` tags. A push to trunk publishes
  to the private `vault-packages` feed (`src/nuget.config`, secret `NUGET_PAT`); nuget.org is
  promoted from that feed by the n2-publishing repository.
