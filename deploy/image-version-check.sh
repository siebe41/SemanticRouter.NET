#!/usr/bin/env bash
# Whether a container image is the version it was built as, for CI and the release (docs/releases.md): what
# `semantic-router version` prints, what the running router reports to the dashboard (libraryVersion in
# GET /router/api/status), and the image's OCI version and revision labels.
#
#   deploy/image-version-check.sh <image> <version> <revision>
#
# Starts the image on a free loopback port with an admin token, asks for its status and removes it again. Prints what
# differs as ::error:: annotations and exits 1 when anything does.
set -euo pipefail

image="$1" version="$2" revision="$3"
token="image-version-check-0123456789"
name="image-version-check-$$"
failed=false

mismatch() {
  echo "::error::$1 is '$2', expected '$3'."
  failed=true
}

labels="$(docker image inspect "$image" --format '{{json .Config.Labels}}')"
for pair in "org.opencontainers.image.version=$version" "org.opencontainers.image.revision=$revision"; do
  actual="$(jq -r --arg key "${pair%%=*}" '.[$key] // ""' <<<"$labels")"
  [ "$actual" = "${pair#*=}" ] || mismatch "The label ${pair%%=*}" "$actual" "${pair#*=}"
done

actual="$(docker run --rm "$image" version | tr -d '\r')"
[ "$actual" = "$version" ] || mismatch "semantic-router version" "$actual" "$version"

trap 'docker rm -f "$name" >/dev/null 2>&1 || true' EXIT
docker run -d --name "$name" -p 127.0.0.1::8080 -e ROUTER_ADMIN_TOKEN="$token" "$image" >/dev/null
port="$(docker port "$name" 8080/tcp | head -n 1)"
port="${port##*:}"
status=""
for _ in $(seq 1 60); do
  status="$(curl -sf -H "Authorization: Bearer $token" "http://127.0.0.1:$port/router/api/status")" && break
  sleep 1
done
if [ -z "$status" ]; then
  echo "::error::$image did not answer GET /router/api/status within a minute."
  docker logs "$name"
  exit 1
fi
actual="$(jq -r '.libraryVersion // ""' <<<"$status")"
[ "$actual" = "$version" ] || mismatch "libraryVersion in GET /router/api/status" "$actual" "$version"

if [ "$failed" = true ]; then
  exit 1
fi
echo "$image is $version ($revision)."
