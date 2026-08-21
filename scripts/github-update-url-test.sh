#!/usr/bin/env bash
# github-update-url-test.sh - unit test for the update helper's URL guards.
#
#   scripts/github-update-url-test.sh
#
# packaging/packetnet-github-update runs as ROOT and curls + `dpkg -i`s whatever the request
# spool names, and that spool is writable by the unprivileged `packetnet` user. Its two guards
# are therefore the boundary between that user and root:
#
#   --check-url         the download host must be a GitHub release host
#   --check-health-url  the post-install health gate must be a loopback /healthz
#
# The original download guard globbed the WHOLE url with `case`, where `*` also matches `/`,
# so the allow-listed text only had to appear in the PATH: https://evil.example/x.github.com/
# p.deb passed (packet.net#699 / C075). The bypass URLs below are that finding, frozen as
# tests. Both guards are pure and side-effect free, so this needs no network, no root and no
# dpkg host - it just invokes the helper's validate-only modes.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
helper="$here/../packaging/packetnet-github-update"
[ -x "$helper" ] || { echo "not executable: $helper" >&2; exit 2; }

fails=0

# expect_reject <mode> <url> <why>
expect_reject() {
    if "$helper" "$1" "$2" >/dev/null 2>&1; then
        echo "FAIL: $1 ACCEPTED $2 ($3)"
        fails=$((fails + 1))
    else
        echo "  ok (rejected): $2"
    fi
}

# expect_accept <mode> <url> <why>
expect_accept() {
    if "$helper" "$1" "$2" >/dev/null 2>&1; then
        echo "  ok (accepted): $2"
    else
        echo "FAIL: $1 REJECTED $2 ($3)"
        fails=$((fails + 1))
    fi
}

echo "== download URL guard: the C075 bypasses must be rejected =="
expect_reject --check-url 'https://evil.example/x.github.com/p.deb' \
    'C075: allow-listed text in the path, glob * matched the /'
expect_reject --check-url 'https://evil.example/.githubusercontent.com/p.deb' \
    'C075: same, githubusercontent form'

echo "== download URL guard: the neighbouring tricks must be rejected too =="
expect_reject --check-url 'https://github.com@evil.example/p.deb'          'userinfo before the real host'
expect_reject --check-url 'https://evil.example#.github.com/p.deb'        'allow-listed text in the fragment'
expect_reject --check-url 'https://evil.example?x=.github.com/p.deb'      'allow-listed text in the query'
expect_reject --check-url 'https://github.com.evil.example/p.deb'         'allow-listed text as a subdomain label'
expect_reject --check-url 'https://evil.githubusercontent.com.evil.net/p.deb' 'suffix continues past the allow-list'
expect_reject --check-url 'http://github.com/p.deb'                       'plain http'
expect_reject --check-url 'ftp://github.com/p.deb'                        'not https'
expect_reject --check-url 'https://.github.com/p.deb'                     'empty leading label'
expect_reject --check-url 'https:///github.com/p.deb'                     'empty host'
expect_reject --check-url ''                                              'empty URL'

echo "== download URL guard: the real release-asset forms must still be accepted =="
expect_accept --check-url \
    'https://github.com/packet-net/packet.net/releases/download/node-v0.44.0/packetnet_amd64.deb' \
    'the browser_download_url the node spools (version-free asset name)'
expect_accept --check-url \
    'https://github.com/packet-net/packet.net/releases/download/node-v0.40.0/packetnet_0.40.0_amd64.deb' \
    'the same, on a release predating the version-free rename'
expect_accept --check-url \
    'https://objects.githubusercontent.com/github-production-release-asset-2e65be/1234/abcd?X-Amz-Algorithm=AWS4-HMAC-SHA256&actor_id=0' \
    'the signed redirect target, query string and all'
expect_accept --check-url \
    'https://release-assets.githubusercontent.com/github-production-release-asset/1234/packetnet_0.40.0_arm64.deb' \
    'the newer release-assets host'
expect_accept --check-url 'https://GitHub.com/packet-net/packet.net/releases/download/node-v1.0.0/x.deb' \
    'hostnames are case-insensitive'

echo "== health URL guard (C101): loopback /healthz only =="
expect_accept --check-health-url 'http://127.0.0.1:8080/healthz' 'the default'
expect_accept --check-health-url 'http://127.0.0.1:9999/healthz' 'a non-8080 port - the whole point of C101'
expect_accept --check-health-url 'http://[::1]:8080/healthz'     'the IPv6 loopback form'
expect_reject --check-health-url 'http://evil.example:8080/healthz' 'not loopback'
expect_reject --check-health-url 'http://127.0.0.1:8080/'          'not /healthz'
expect_reject --check-health-url 'https://127.0.0.1:8080/healthz'  'the node serves the gate over plain http'
expect_reject --check-health-url 'http://127.0.0.1:0/healthz'      'port 0'
expect_reject --check-health-url 'http://127.0.0.1:99999/healthz'  'port out of range'
expect_reject --check-health-url 'http://127.0.0.1:80x0/healthz'   'non-numeric port'
expect_reject --check-health-url 'http://127.0.0.2:8080/healthz'   'loopback range but not the loopback address'

if [ "$fails" -eq 0 ]; then
    echo "URL_GUARD_PASS"
    exit 0
fi
echo "URL_GUARD_FAIL ($fails)"
exit 1
