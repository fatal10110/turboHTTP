# Go net/http (HTTP/2 through ListenAndServeTLS), built in a pinned golang image and run on a pinned alpine.
FROM golang:1.23-alpine@sha256:383395b794dffa5b53012a212365d40c8e37109a626ca30d6151c8348d380b5f AS build
WORKDIR /src
COPY main.go .
RUN CGO_ENABLED=0 go build -trimpath -o /h2server main.go

FROM alpine:3.20@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc
COPY --from=build /h2server /usr/local/bin/h2server
ENTRYPOINT ["/usr/local/bin/h2server"]
