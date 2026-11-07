// Go net/http HTTP/2 server for the h2-interop rows (Tools~/h2-interop/run.sh mounts the per-run dir on /pki).
// ListenAndServeTLS negotiates h2 through ALPN with the standard library's built-in HTTP/2 server.
package main

import (
	"fmt"
	"io"
	"log"
	"net/http"
	"strings"
)

func main() {
	mux := http.NewServeMux()
	mux.Handle("/", http.FileServer(http.Dir("/pki/www")))

	// Echoes the request body, like nghttpd --echo-upload.
	mux.HandleFunc("/upload", func(w http.ResponseWriter, r *http.Request) {
		body, err := io.ReadAll(r.Body)
		if err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}
		w.Header().Set("Content-Type", "application/octet-stream")
		w.Write(body)
	})

	// x-h-<i> = 200 x chr('a' + i % 26) (InteropRows.ResponseHeaderValue).
	mux.HandleFunc("/headers", func(w http.ResponseWriter, r *http.Request) {
		for i := 0; i < 40; i++ {
			w.Header().Set(fmt.Sprintf("x-h-%d", i), strings.Repeat(string(rune('a'+i%26)), 200))
		}
		io.WriteString(w, "headers\n")
	})

	log.Fatal(http.ListenAndServeTLS(":8443", "/pki/leaf-valid.pem", "/pki/leaf.key", mux))
}
