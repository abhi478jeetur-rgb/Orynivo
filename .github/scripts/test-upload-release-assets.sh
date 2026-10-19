#!/usr/bin/env bash
# Exercises the production uploader against offline command substitutes only.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
FIXTURE="$(mktemp -d)"
trap 'rm -rf "$FIXTURE"' EXIT
mkdir "$FIXTURE/bin"
export FIXTURE GITHUB_REPOSITORY=synthetic/repository GITHUB_SHA=current GH_TOKEN=synthetic
export PATH="$FIXTURE/bin:$PATH"
printf 'synthetic package' > "$FIXTURE/package.deb"

cat > "$FIXTURE/bin/gh" <<'EOF'
#!/usr/bin/env bash
set -eu
printf '%s\n' "$*" >> "$FIXTURE/calls"
case "$*" in
  *commits*)
    if [ "$MODE" = stale ] || { [ "$MODE" = moved ] && [ -f "$FIXTURE/asset" ]; } \
      || { [ "$MODE" = waiting ] && [ -f "$FIXTURE/waiting" ]; }; then
      echo obsolete
    else
      echo current
    fi ;;
  *'--method DELETE'*) rm -f "$FIXTURE/asset" ;;
  *'/assets'*) [ ! -f "$FIXTURE/asset" ] || echo 77 ;;
  *'--method POST'*) echo 42 ;;
  *'/releases?'*)
    if [ "$MODE" = waiting ]; then touch "$FIXTURE/waiting"
    elif [ "$MODE" != create ]; then echo 42; fi ;;
  *) echo 'Unexpected synthetic API request' >&2; exit 1 ;;
esac
EOF
cat > "$FIXTURE/bin/curl" <<'EOF'
#!/usr/bin/env bash
set -eu
printf 'upload\n' >> "$FIXTURE/calls"
if [ -f "$FIXTURE/asset" ]; then exit 22; fi
touch "$FIXTURE/asset"
if [ "$MODE" = interrupted ] || [ "$MODE" = moved ]; then
  if [ ! -f "$FIXTURE/interrupted" ]; then
    touch "$FIXTURE/interrupted"
    exit 22
  fi
fi
[ "$MODE" != failure ] || exit 22
EOF
cat > "$FIXTURE/bin/jq" <<'EOF'
#!/usr/bin/env bash
printf 'package.deb\n'
EOF
cat > "$FIXTURE/bin/sleep" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
chmod +x "$FIXTURE/bin/"*

cat > "$FIXTURE/bin/git" <<'EOF'
#!/usr/bin/env bash
[ "$*" = 'rev-parse HEAD' ] || exit 1
echo current
EOF
chmod +x "$FIXTURE/bin/git"

for MODE in success existing interrupted stale moved waiting create dispatch failure; do
  export MODE
  export GITHUB_SHA=current
  [ "$MODE" != dispatch ] || export GITHUB_SHA=unrelated-event-commit
  rm -f "$FIXTURE/asset" "$FIXTURE/interrupted" "$FIXTURE/calls" "$FIXTURE/waiting"
  [ "$MODE" != existing ] || touch "$FIXTURE/asset"
  status=0
  create=false
  [ "$MODE" != create ] || create=true
  bash "$SCRIPT_DIR/upload-release-assets.sh" v1.0.0 'Synthetic release' "$create" \
    "$FIXTURE/package.deb" < /dev/null > "$FIXTURE/output" 2>&1 || status=$?
  case "$MODE" in
    success|existing|interrupted|create|dispatch)
      [ "$status" -eq 0 ] && [ -f "$FIXTURE/asset" ] ;;
    stale)
      [ "$status" -eq 2 ]
      ! grep -Eq 'upload|DELETE|releases' "$FIXTURE/calls" ;;
    moved)
      [ "$status" -eq 2 ]
      [ "$(grep -c '^upload$' "$FIXTURE/calls")" -eq 1 ]
      ! grep -q DELETE "$FIXTURE/calls" ;;
    waiting)
      [ "$status" -eq 2 ]
      ! grep -Eq 'upload|DELETE|POST' "$FIXTURE/calls" ;;
    failure)
      [ "$status" -eq 1 ]
      [ "$(grep -c '^upload$' "$FIXTURE/calls")" -eq 5 ] ;;
  esac
  if [ "$MODE" = interrupted ]; then
    [ "$(grep -c '^upload$' "$FIXTURE/calls")" -eq 2 ]
    [ "$(grep -c DELETE "$FIXTURE/calls")" -eq 1 ]
  fi
  echo "PASS: release upload $MODE"
done
