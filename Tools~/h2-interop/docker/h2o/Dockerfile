# h2o 2.2.5 (Debian bookworm's h2o package, built with mruby) on the pinned base image nghttpd uses. No official
# h2o image is pullable anonymously; the distribution package keeps the build short and the version fixed.
FROM debian:bookworm-slim@sha256:99c0b25c46d0214d5a2091092e7908907dcaecd6a962cde0ac700acee222b6f3
RUN apt-get update \
 && apt-get install -y --no-install-recommends h2o \
 && rm -rf /var/lib/apt/lists/*
COPY h2o.conf /etc/h2o/h2o.conf
ENTRYPOINT ["h2o", "-c", "/etc/h2o/h2o.conf"]
