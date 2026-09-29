#!/usr/bin/env bash
# Smoke test of a running ClubShell server (docs/server/DESIGN.md §11 S6): /health, register a PC, signed heartbeat.
#
#   CLUB_KEY=<Club__EnrollmentKey> server/scripts/smoke.sh [base-url]      # default http://localhost:8080
#
# The server must run with Club__AutoApprovePcs=true (otherwise register answers 403 pendingApproval, by design).
# Needs curl, openssl, jq, base64, od, sha256sum. The signature is the agent's: HMAC-SHA256 over
# timestamp + METHOD + target + sha256(body), keyed with the base64-decoded signingSecret (server/src/.../RequestSignature.cs).
set -euo pipefail

base="${1:-http://localhost:8080}"
key="${CLUB_KEY:?set CLUB_KEY to the Club__EnrollmentKey of the server}"
tmp="$(mktemp)"
trap 'rm -f "$tmp"' EXIT

fail() { echo "smoke: $*" >&2; [ -s "$tmp" ] && sed 's/^/smoke:   /' "$tmp" >&2; exit 1; }

health_code() { curl -s -o /dev/null -w '%{http_code}' "$base/health" || true; }

deadline=$((SECONDS + 60))
until [ "$(health_code)" = 200 ]; do
  [ "$SECONDS" -lt "$deadline" ] || fail "GET /health was not 200 within 60 s"
  sleep 2
done
echo "smoke: GET /health -> 200"

hwid="$(openssl rand -hex 32)"
mac="$(openssl rand -hex 6 | sed 's/../&:/g; s/:$//' | tr 'a-f' 'A-F')"
register="{\"hwid\":\"$hwid\",\"machineName\":\"SMOKE-PC\",\"agentVersion\":\"1.4.2\",\"hardware\":{\"cpu\":{\"model\":\"Ryzen 5 5600\",\"cores\":6,\"threads\":12},\"gpu\":[],\"ramMb\":16384,\"disks\":[],\"monitors\":[],\"network\":{\"mac\":\"$mac\",\"ip\":\"10.0.0.12\",\"adapter\":\"Ethernet\"},\"os\":{\"version\":\"10.0.22631\",\"build\":\"22631\"},\"peripherals\":[]},\"ipAddress\":\"10.0.0.12\",\"macAddress\":\"$mac\"}"

code="$(curl -s -o "$tmp" -w '%{http_code}' -X POST "$base/api/v1/agents/register" \
  -H "X-Club-Key: $key" -H 'Content-Type: application/json' --data-binary "$register")"
[ "$code" = 200 ] || fail "POST /api/v1/agents/register -> $code (expected 200; is Club__AutoApprovePcs=true and the key right?)"
pc="$(jq -r .pcId "$tmp")"
token="$(jq -r .accessToken "$tmp")"
secret_hex="$(jq -r .signingSecret "$tmp" | base64 -d | od -An -v -tx1 | tr -d ' \n')"
[ -n "$pc" ] && [ "$pc" != null ] && [ -n "$secret_hex" ] || fail "register response has no pcId / signingSecret"
echo "smoke: POST /api/v1/agents/register -> 200 (pc $pc)"

target="/api/v1/agents/$pc/heartbeat"
body='{"status":"free","agentVersion":"1.4.2","shellVersion":"1.4.2","uptimeSec":3600,"ipAddress":"10.0.0.12","policyVersion":0,"runningGames":[],"offlineQueue":0,"shellConnected":true}'
ts="$(date +%s)"
body_hash="$(printf '%s' "$body" | sha256sum | cut -d' ' -f1)"
sig="$(printf '%s' "${ts}POST${target}${body_hash}" | openssl dgst -sha256 -mac HMAC -macopt "hexkey:$secret_hex" -r | cut -d' ' -f1)"

code="$(curl -s -o "$tmp" -w '%{http_code}' -X POST "$base$target" \
  -H "Authorization: Bearer $token" -H "X-Timestamp: $ts" -H "X-Signature: $sig" \
  -H 'Content-Type: application/json' --data-binary "$body")"
[ "$code" = 200 ] || fail "signed POST $target -> $code (expected 200)"
echo "smoke: signed POST $target -> 200"
echo "smoke: ok"
