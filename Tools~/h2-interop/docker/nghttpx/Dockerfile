# nghttpx (Debian bookworm's nghttp2-proxy) terminating TLS + ALPN h2 in front of nghttpd (nghttp2-server) over
# cleartext h2 inside the container, on the pinned base image docker/nghttpd uses.
FROM debian:bookworm-slim@sha256:99c0b25c46d0214d5a2091092e7908907dcaecd6a962cde0ac700acee222b6f3
RUN apt-get update \
 && apt-get install -y --no-install-recommends nghttp2-proxy nghttp2-server \
 && rm -rf /var/lib/apt/lists/*
COPY entrypoint.sh /etc/nghttpx/
ENTRYPOINT ["/bin/sh", "/etc/nghttpx/entrypoint.sh"]
