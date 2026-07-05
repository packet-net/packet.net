# packetnet-headend — split-station RF head-end daemon.
#
# Static, CGO-free, cross-compiled per Pi arch. `make` builds the arm targets;
# `make check` runs the local gate (fmt + vet + test).

BIN     := packetnet-headend
DIST    := dist
LDFLAGS := -s -w
GOFLAGS := -trimpath -ldflags="$(LDFLAGS)"

.PHONY: all arm64 arm amd64 check test vet fmt clean

# Default: the two Raspberry Pi targets.
all: arm64 arm

arm64:
	CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build $(GOFLAGS) -o $(DIST)/$(BIN)-linux-arm64 .

# 32-bit ARM v7 (Pi 2 / Zero 2 W 32-bit userland).
arm:
	CGO_ENABLED=0 GOOS=linux GOARCH=arm GOARM=7 go build $(GOFLAGS) -o $(DIST)/$(BIN)-linux-arm .

# Host arch, handy for running the daemon on a dev box.
amd64:
	CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build $(GOFLAGS) -o $(DIST)/$(BIN)-linux-amd64 .

# Local green gate (mirrors what CI would run in Stage 4).
check: fmt vet test

test:
	go test ./...

vet:
	go vet ./...

# Fails if any file is not gofmt-clean.
fmt:
	@unformatted="$$(gofmt -l .)"; \
	if [ -n "$$unformatted" ]; then echo "gofmt needed on:"; echo "$$unformatted"; exit 1; fi

clean:
	rm -rf $(DIST)
