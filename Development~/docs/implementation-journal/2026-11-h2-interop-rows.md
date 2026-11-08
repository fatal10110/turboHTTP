# 2026-11 h2-interop: h2o, Go net/http, Envoy and nghttpx rows (#288)

## What

The h2-interop job (`Tools~/h2-interop/run.sh`, CI job `h2-interop`) now runs the interop rows against seven
servers instead of three: Kestrel, nghttpd and nginx as before, plus h2o, Go net/http, Envoy and nghttpx. Each
new server runs in docker, listens on 8443 and serves the per-run `/pki/www` files with `/pki/leaf-valid.pem`
and `/pki/leaf.key`, over TLS with ALPN h2. Every image is pinned by digest.

The ad-hoc `server != "nginx"` / `server != "nghttpd"` comparisons became a capability table
(`InteropRows.Servers`: echoes `/upload`, serves `/headers`). A row a server cannot serve prints
`N/A <row>: <reason>` instead of disappearing. A known-failure mechanism now exists
(`InteropRows.KnownFailures`, row name to issue number): a listed row that fails prints
`KNOWN-FAIL <row> (#N)` and does not fail the run, and a listed row that passes fails the run, so a stale entry
cannot hide a fix. The list is empty: no row found a client defect.

## Files

- `Tools~/h2-interop/run.sh`: pulls nginx and Envoy by digest, builds the four Dockerfiles, starts each server
  through `start <name> ...`, runs `interop` for each, and writes `docker-<server>.log`.
- `Tools~/h2-interop/InteropRows.cs`: the `Servers` capability table, `KnownFailures`, the `N/A` line, and
  `www/headers` (nghttpx serves `/headers` from it).
- `Tools~/h2-interop/Program.cs`: `RunRowsAsync` takes the known-failure map and prints `KNOWN-FAIL`. The
  summary line counts known failures.
- `Tools~/h2-interop/docker/h2o/` (Dockerfile, h2o.conf), `docker/go/` (Dockerfile, main.go),
  `docker/envoy/envoy.yaml`, `docker/nghttpx/` (Dockerfile, entrypoint.sh).
- `Tools~/h2-interop/README.md` (server table, result lines), `.github/workflows/ci.yml` (job comment).

## Decisions

- **h2o image.** No official h2o image is pullable anonymously (ghcr.io/h2o/h2o and h2oserver/h2o refuse
  anonymous pulls). The only Docker Hub candidate found, `lkwg82/h2o-http2-server`, is a third-party image of h2o
  2.2.6. h2o therefore comes from Debian bookworm's `h2o` package (2.2.5, built with mruby, OpenSSL 3), on the
  same pinned `debian:bookworm-slim` digest that nghttpd uses. This matches the nghttpd pattern and keeps the
  build short. mruby handlers echo `/upload` and send the 40 `/headers` fields.
- **Go.** A ~40-line `net/http` server (`ListenAndServeTLS`, built-in HTTP/2) is built in pinned
  `golang:1.23-alpine` and copied onto pinned `alpine:3.20`. `/upload` reads the whole body and then echoes it.
  `/headers` sets the 40 fields in code.
- **Envoy** (`envoyproxy/envoy:v1.32.1`, pinned). Envoy has no static-file serving, so each path is a
  `direct_response`. The two files are read from `/pki/www` at startup, and
  `max_direct_response_body_size_bytes` is raised to 3 MiB for `large.bin`. `/upload` answers `uploaded\n`
  without reading the body. This exercises the RFC 9113 8.1 early complete response, and the client passes it.
  `/headers` fields are added by a Lua filter, so no 40-line list is committed. `ENVOY_UID=0` keeps Envoy root
  inside the container (as the other containers are) so it can read the mounted key.
- **nghttpx.** Debian `nghttp2-proxy` terminates TLS + ALPN h2 and forwards over cleartext h2 to nghttpd
  (`nghttp2-server`, `--echo-upload`) in the same container. Debian builds nghttpx without mruby (its log says
  so), so `--add-response-header` appends the 40 fields to every response. `/headers` is the `www/headers` file
  served by nghttpd behind it. This gives nghttp2's HPACK encoder the `response-headers-many` row that nghttpd
  alone cannot run.
- **CI time.** The cold interop phase (Envoy pull, four image builds, seven servers) took about 64 s locally,
  and a warm one took 20 s. That is well inside the job's `timeout-minutes: 20`, so no server moved to a
  scheduled workflow.

## Results

`'Tools~/h2-interop/run.sh' --only interop`: Kestrel 9/9, nghttpd 8/8 (+1 N/A), nginx 9/9, h2o 9/9, go 9/9,
envoy 9/9, nghttpx 9/9. That is 62 PASS, 0 FAIL and 0 KNOWN-FAIL. Mechanism checks were done with temporary edits,
none committed:
- A `KnownFailures` entry on a passing row failed the run with "passed, but is listed as a known failure".
- An entry on a forced failure printed `KNOWN-FAIL ... (#1)` and the server's summary was "0/1 rows passed, 1
  known failure(s)".

## Deferrals

None. No defect was found, so no v1.0-beta issue was filed.

## Not run

Tooling only, no Runtime change: Unity EditMode/PlayMode, IL2CPP and device runs are not applicable and were not
run. The interop rows run on .NET 8 (CoreCLR `SslStream` with OpenSSL), not on Mono or IL2CPP.
