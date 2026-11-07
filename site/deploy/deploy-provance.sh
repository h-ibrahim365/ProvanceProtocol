#!/usr/bin/env bash
# Deploys the PROVANCE demo site. Run by the webhook when the CI workflow succeeds on main, with the commit as
# the only argument. Can also be run by hand: sudo -u provance /usr/local/bin/deploy-provance.sh [commit]
#
# Builds the site and the status service on this server, then swaps them in. If the service does not come back
# healthy, the previous version is put back.

set -Eeuo pipefail

BASE=/opt/provance
SRC=$BASE/source
LOG=/var/log/provance/deploy.log
REPO=https://github.com/h-ibrahim365/ProvanceProtocol.git

exec >>"$LOG" 2>&1
log() { echo "$(date -Is) $*"; }
trap 'log "FAILED at line $LINENO"' ERR

SHA="${1:-}"
if [[ -n "$SHA" && ! "$SHA" =~ ^[0-9a-f]{40}$ ]]; then
  log "refusing commit argument '$SHA'"
  exit 1
fi

# One deployment at a time; a second webhook waits for the first one.
exec 9>"$BASE/deploy.lock"
flock 9

export PATH="$HOME/.cargo/bin:$PATH"

[[ -d "$SRC/.git" ]] || git clone --quiet "$REPO" "$SRC"
git -C "$SRC" fetch --quiet origin main
SHA="${SHA:-$(git -C "$SRC" rev-parse origin/main)}"
git -C "$SRC" checkout --quiet --force --detach "$SHA"
log "deploying $SHA"

# Pushes that only touch the libraries do not redeploy the site.
LAST=$(cat "$BASE/deployed-sha" 2>/dev/null || true)
if [[ -n "$LAST" ]] && git -C "$SRC" cat-file -e "$LAST" 2>/dev/null \
  && git -C "$SRC" diff --quiet "$LAST" "$SHA" -- site docs/test-vectors; then
  log "no change under site/ or docs/test-vectors/ since $LAST, nothing to do"
  echo "$SHA" >"$BASE/deployed-sha"
  exit 0
fi

cd "$SRC/site"
npm ci --no-audit --no-fund --loglevel=error
npm test --silent
npm run build --silent

cd "$SRC/site/server"
cargo build --release --locked --quiet

# Stage, then swap.
rm -rf "$BASE/web-next" "$BASE/web-prev"
cp -a "$SRC/site/dist" "$BASE/web-next"
chmod -R a+rX "$BASE/web-next"
install -m 755 target/release/provance-status "$BASE/bin/provance-status.next"

[[ -d "$BASE/web" ]] && mv "$BASE/web" "$BASE/web-prev"
mv "$BASE/web-next" "$BASE/web"
[[ -f "$BASE/bin/provance-status" ]] && cp -a "$BASE/bin/provance-status" "$BASE/bin/provance-status.prev"
mv "$BASE/bin/provance-status.next" "$BASE/bin/provance-status"
sudo /usr/bin/systemctl restart provance-status 9>&-

healthy=false
for _ in $(seq 1 15); do
  if curl -fsS --max-time 2 http://127.0.0.1:8787/api/health >/dev/null; then healthy=true; break; fi
  sleep 1
done

if ! $healthy; then
  log "status service not healthy, rolling back"
  if [[ -d "$BASE/web-prev" ]]; then rm -rf "$BASE/web"; mv "$BASE/web-prev" "$BASE/web"; fi
  if [[ -f "$BASE/bin/provance-status.prev" ]]; then mv "$BASE/bin/provance-status.prev" "$BASE/bin/provance-status"; fi
  sudo /usr/bin/systemctl restart provance-status 9>&-
  exit 1
fi

echo "$SHA" >"$BASE/deployed-sha"
log "deployed $SHA"
