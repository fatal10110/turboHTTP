#!/bin/sh
# nghttpd (echoes uploads, serves /pki/www) on cleartext h2 port 8080; nghttpx terminates TLS + ALPN h2 on 8443
# and forwards over h2. Debian's nghttpx has no mruby, so it appends x-h-<i> = 200 x chr('a' + i % 26)
# (InteropRows.ResponseHeaderValue) to every response; /headers is the www/headers file.
set -eu
set --
i=0
while [ "$i" -lt 40 ]; do
  c=$(echo abcdefghijklmnopqrstuvwxyz | cut -c $((i % 26 + 1)))
  set -- "$@" "--add-response-header=x-h-$i: $(printf '%200s' '' | tr ' ' "$c")"
  i=$((i + 1))
done
nghttpd --no-tls --echo-upload --htdocs=/pki/www 8080 &
exec nghttpx --conf=/dev/null --no-ocsp --workers=1 --frontend='*,8443' --backend='127.0.0.1,8080;;proto=h2' \
  "$@" /pki/leaf.key /pki/leaf-valid.pem
