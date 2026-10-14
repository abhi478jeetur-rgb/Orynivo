#!/usr/bin/env bash
set -euo pipefail

TAG="$1"
RELEASE_NAME="$2"
CREATE_DRAFT="$3"
shift 3

# Dispatch can start on main and then check out an older release tag. Compare
# against the checkout actually used for the packages, not the event's SHA.
BUILD_COMMIT="$(git rev-parse HEAD)"

# GitHub occasionally answers a transient 5xx for release API and asset upload
# requests. Retry a bounded number of times with backoff instead of failing the
# complete release.
MAX_ATTEMPTS=5

retry() {
  local attempt=1
  while true; do
    local status=0
    "$@" || status=$?
    if [ "$status" -eq 0 ]; then
      return 0
    fi
    # A moved release tag is terminal: retrying must never publish an old build.
    [ "$status" -ne 2 ] || return 2
    if [ "$attempt" -ge "$MAX_ATTEMPTS" ]; then
      echo "Command failed after ${MAX_ATTEMPTS} attempts: $*" >&2
      return 1
    fi
    local delay=$((attempt * 5))
    echo "Command failed (attempt ${attempt}/${MAX_ATTEMPTS}); retrying in ${delay}s: $*" >&2
    sleep "$delay"
    attempt=$((attempt + 1))
  done
}

assert_current_tag() {
  local current_sha
  current_sha="$(gh api "repos/${GITHUB_REPOSITORY}/commits/${TAG}" --jq .sha)" || return 1
  if [ "$current_sha" != "$BUILD_COMMIT" ]; then
    echo "Release tag changed after this workflow started; refusing stale assets." >&2
    return 2
  fi
}

retry assert_current_tag

find_release_id() {
  retry gh api "repos/${GITHUB_REPOSITORY}/releases?per_page=100" \
    --jq "[.[] | select(.draft and .tag_name == \"${TAG}\" and .name == \"${RELEASE_NAME}\")][0].id // empty"
}

RELEASE_ID="$(find_release_id)"
if [ -z "$RELEASE_ID" ] && [ "$CREATE_DRAFT" = "true" ]; then
  retry assert_current_tag
  RELEASE_ID="$(retry gh api --method POST "repos/${GITHUB_REPOSITORY}/releases" \
    -f tag_name="$TAG" \
    -f name="$RELEASE_NAME" \
    -F draft=true \
    -F generate_release_notes=false \
    -f body="$(cat)" \
    --jq .id)"
fi

if [ -z "$RELEASE_ID" ]; then
  for ATTEMPT in $(seq 1 60); do
    retry assert_current_tag
    RELEASE_ID="$(find_release_id)"
    [ -n "$RELEASE_ID" ] && break
    echo "Canonical draft '${RELEASE_NAME}' is not available yet (attempt ${ATTEMPT}/60)."
    sleep 10
  done
fi

if [ -z "$RELEASE_ID" ]; then
  echo "Canonical draft '${RELEASE_NAME}' for tag '${TAG}' was not found." >&2
  exit 1
fi

upload_asset() {
  assert_current_tag || return $?
  # An interrupted upload can already exist despite an HTTP error. Reconcile
  # before every retry, rather than retrying a duplicate POST indefinitely.
  local existing_id
  existing_id="$(gh api "repos/${GITHUB_REPOSITORY}/releases/${RELEASE_ID}/assets" \
    --jq "[.[] | select(.name == \"${NAME}\")][0].id // empty")" || return 1
  if [ -n "$existing_id" ]; then
    gh api --method DELETE "repos/${GITHUB_REPOSITORY}/releases/assets/${existing_id}" || return 1
  fi
  curl --fail --silent --show-error \
    --request POST \
    --header "Authorization: Bearer ${GH_TOKEN}" \
    --header 'Accept: application/vnd.github+json' \
    --header 'X-GitHub-Api-Version: 2022-11-28' \
    --header 'Content-Type: application/octet-stream' \
    --data-binary "@${FILE}" \
    "https://uploads.github.com/repos/${GITHUB_REPOSITORY}/releases/${RELEASE_ID}/assets?name=${ENCODED_NAME}" \
    > /dev/null
}

for FILE in "$@"; do
  NAME="$(basename "$FILE")"
  ENCODED_NAME="$(jq -rn --arg value "$NAME" '$value|@uri')"
  retry upload_asset
  echo "Uploaded ${NAME} to release ${RELEASE_ID}."
done
