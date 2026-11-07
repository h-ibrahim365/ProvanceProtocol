# Deploying the demo site

The site is served by Caddy on the VPS. The Releases page reads `/api/status` from `provance-status`, a small Rust
service that polls GitHub, NuGet, Maven Central and PyPI and keeps the answers in memory.

Deployment follows a GitHub webhook, like the other sites on the server:

```
push to main ─> CI succeeds ─> GitHub sends "workflow_run completed"
             ─> Caddy /hooks/provance-deploy ─> provance-webhook (127.0.0.1:9002, checks the signature)
             ─> /usr/local/bin/deploy-provance.sh <commit>
                  pulls, tests and builds site/ and site/server/ on the server,
                  swaps /opt/provance/web and the binary, restarts provance-status,
                  puts the previous version back if the service is not healthy
```

Only a CI run that succeeded on a push to `main` deploys. Pushes that do not touch `site/` or
`docs/test-vectors/` are skipped.

| What | Where |
| :--- | :--- |
| Source checkout | `/opt/provance/source` |
| Served site | `/opt/provance/web` |
| Status service binary | `/opt/provance/bin/provance-status` |
| Deploy script | `/usr/local/bin/deploy-provance.sh` (copy of `deploy-provance.sh`) |
| Webhook config | `/etc/webhook/provance-hooks.json` |
| Caddy site | `/etc/caddy/sites/provance.caddy` |
| Deploy log | `/var/log/provance/deploy.log` |

## One-time setup

Commands run as root on the server. The domain is `provance.iamhakim.com`; to use another one, change the first
line of `provance.caddy`.

### 1. Check that the ports are free

```bash
ss -tlnp | grep -E ':(9002|8787)\b'     # must print nothing
```

### 2. DNS (Cloudflare)

Same as `whoami`: in the `iamhakim.com` zone, add a `CNAME` record `provance` pointing to `iamhakim.com`, Proxied.

### 3. User, folders, toolchains

```bash
adduser --system --group --home /opt/provance --shell /bin/bash provance
mkdir -p /opt/provance/bin /var/log/provance
chown -R provance:provance /opt/provance /var/log/provance
chmod 755 /opt/provance     # adduser makes it 750; Caddy and the status service must be able to enter it
echo 'provance ALL=(root) NOPASSWD: /usr/bin/systemctl restart provance-status' > /etc/sudoers.d/provance
chmod 440 /etc/sudoers.d/provance && visudo -c

apt install -y git curl build-essential
node --version     # 22.12 or later; otherwise install Node 22
sudo -u provance -H bash -c 'curl --proto =https --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal'
sudo -u provance -H git clone https://github.com/h-ibrahim365/ProvanceProtocol.git /opt/provance/source
```

### 4. Services, webhook and Caddy site

```bash
cd /opt/provance/source/site/deploy
install -m 755 deploy-provance.sh /usr/local/bin/deploy-provance.sh
install -m 644 provance-status.service provance-webhook.service /etc/systemd/system/
install -m 644 provance.caddy /etc/caddy/sites/provance.caddy

mkdir -p /etc/webhook
SECRET=$(openssl rand -hex 32)
sed "s/REPLACE_WITH_THE_WEBHOOK_SECRET/$SECRET/" provance-hooks.json > /etc/webhook/provance-hooks.json
chown root:provance /etc/webhook/provance-hooks.json && chmod 640 /etc/webhook/provance-hooks.json
echo "$SECRET"      # paste it in GitHub at step 8

systemctl daemon-reload
systemctl enable provance-status provance-webhook
systemctl start provance-webhook
```

Optional, recommended: a GitHub token raises the API limit from 60 to 5000 requests per hour, so the Releases page
refreshes every 10 seconds during a release instead of every 90. A fine-grained token with access to public
repositories only and no permissions is enough:

```bash
install -m 600 /dev/null /etc/provance-status.env
nano /etc/provance-status.env      # GITHUB_TOKEN=github_pat_...
```

### 5. First deployment, by hand

```bash
sudo -u provance -H /usr/local/bin/deploy-provance.sh
tail -n 20 /var/log/provance/deploy.log      # ends with "deployed <commit>"
curl -s http://127.0.0.1:8787/api/health      # ok
```

The first run takes a few minutes (Rust build). Later runs reuse the build cache.

### 6. Caddy

```bash
caddy validate --config /etc/caddy/Caddyfile && systemctl reload caddy
curl -sI https://provance.iamhakim.com | head -1      # HTTP/2 200
```

If Caddy cannot get its certificate (`journalctl -u caddy -n 50` mentions ACME errors), switch the record to DNS
only (grey cloud), reload Caddy, then switch it back to Proxied once the site answers.

### 7. Cloudflare

Nothing else to set: the zone already runs in SSL mode Full (strict), and Web Analytics is injected automatically
on proxied subdomains (the Caddy CSP allows its script).

### 8. GitHub webhook

Repository Settings, Webhooks, Add webhook:

* Payload URL: `https://provance.iamhakim.com/hooks/provance-deploy`
* Content type: `application/json`
* Secret: the value printed at step 4
* SSL verification: enabled
* Events: "Let me select individual events", untick Pushes, tick **Workflow runs** only

GitHub sends a ping first; the listener answers "Hook rules were not satisfied", which is expected.

## Day to day

```bash
journalctl -u provance-webhook -f           # webhooks received
tail -f /var/log/provance/deploy.log         # deployments
systemctl status provance-status
sudo -u provance -H /usr/local/bin/deploy-provance.sh    # redeploy main by hand
```

When `deploy-provance.sh`, the units or `provance.caddy` change in the repo, copy them again as in step 4: like
for iamhakim.com, the files outside `/opt/provance` are not updated by a deployment.
