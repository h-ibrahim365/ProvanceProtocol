# Deploying the demo site

The site is static files served by Caddy. The Releases page reads `/api/status` from `provance-status`, a small
Rust service that polls GitHub, NuGet, Maven Central and PyPI and keeps the answers in memory. Visitors never call
those APIs directly.

```
browser ── HTTPS ──> Caddy ──> /var/www/provance-demo        (static site)
                          └──> 127.0.0.1:8787 /api/*         (provance-status)
```

The GitHub workflow `deploy-site.yml` builds both on every push to `main` that touches `site/`, then copies them to
the server over SSH. The steps below are done once.

Commands assume Ubuntu or Debian, run as root on the server. Replace `demo.example.com` with your domain.

## 1. DNS

Create an `A` record (and `AAAA` if the server has IPv6) for the domain, pointing to the server.

## 2. Caddy

Skip this if another web server already listens on ports 80 and 443: add a site and a reverse proxy rule for
`/api/` to it instead, using `Caddyfile` as the reference.

```bash
apt install -y debian-keyring debian-archive-keyring apt-transport-https curl
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' > /etc/apt/sources.list.d/caddy-stable.list
apt update && apt install -y caddy
```

Copy `Caddyfile` to `/etc/caddy/Caddyfile` (or add its block to the existing one), set the domain, then
`systemctl reload caddy`. Caddy gets the certificate on its own.

## 3. Deploy user and folders

A user that can only write the two folders and restart the service:

```bash
adduser --disabled-password --gecos "" deploy
mkdir -p /var/www/provance-demo /opt/provance-status
chown deploy:deploy /var/www/provance-demo /opt/provance-status
echo 'deploy ALL=(root) NOPASSWD: /usr/bin/systemctl restart provance-status' > /etc/sudoers.d/provance-deploy
chmod 440 /etc/sudoers.d/provance-deploy
apt install -y rsync
```

Key pair for GitHub Actions:

```bash
ssh-keygen -t ed25519 -N "" -C github-deploy -f /root/provance-deploy
install -d -m 700 -o deploy -g deploy /home/deploy/.ssh
cat /root/provance-deploy.pub >> /home/deploy/.ssh/authorized_keys
chown deploy:deploy /home/deploy/.ssh/authorized_keys && chmod 600 /home/deploy/.ssh/authorized_keys
```

## 4. Service

```bash
cp provance-status.service /etc/systemd/system/
systemctl daemon-reload
systemctl enable provance-status
```

Optional but recommended: a GitHub token raises the API limit from 60 to 5000 requests per hour, so the page
updates every 10 seconds during a release instead of every 90. Create a fine-grained token with access to public
repositories only and no permissions, then:

```bash
install -m 600 /dev/null /etc/provance-status.env
nano /etc/provance-status.env      # GITHUB_TOKEN=github_pat_...
```

The service starts with the first deployment.

## 5. GitHub

In the repository settings, create an environment named `demo-site`, then these secrets (from your computer, with
`gh`, so they never appear on a command line):

```bash
ssh root@demo.example.com cat /root/provance-deploy | gh secret set SITE_SSH_KEY --env demo-site
ssh-keyscan -t ed25519 demo.example.com | gh secret set SITE_KNOWN_HOSTS --env demo-site
gh secret set SITE_HOST --env demo-site --body demo.example.com
gh secret set SITE_USER --env demo-site --body deploy
```

On an ARM server (Hetzner CAX), also: `gh variable set VPS_ARCH --body arm64`.

Then delete the private key from the server (`rm /root/provance-deploy`) and run the workflow once:
`gh workflow run deploy-site.yml`.

## Checking

```bash
systemctl status provance-status
journalctl -u provance-status -f
curl -s http://127.0.0.1:8787/api/status | head -c 400
```

If a registry does not answer, the page keeps its last known data and shows which source is down.
