# nghttpd (nghttp2's HTTP/2 server) from Debian bookworm's nghttp2-server package, on a pinned base image.
FROM debian:bookworm-slim@sha256:99c0b25c46d0214d5a2091092e7908907dcaecd6a962cde0ac700acee222b6f3
RUN apt-get update \
 && apt-get install -y --no-install-recommends nghttp2-server \
 && rm -rf /var/lib/apt/lists/*
ENTRYPOINT ["nghttpd"]
