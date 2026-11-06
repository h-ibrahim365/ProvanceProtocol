# PROVANCE demo site

An interactive explanation of the ledger: record events, tamper with the table, verify, cut off the end and catch
it with a checkpoint, erase a payload. Everything in the demo runs in the browser.

* `src/lib/provance/`: ledger format 1 in TypeScript. Tested against `docs/test-vectors/v1.json` and the ledger
  written by the Java library, like the .NET, Java and Python implementations.
* `src/lib/demo/`: the guided steps and the table.
* `src/lib/versions.ts`: the versions in the version menu. When a release adds something worth showing, add a
  version there with its steps and code samples.
* `src/lib/releases/`: the Releases page, fed by `/api/status`.
* `server/`: `provance-status`, the Rust service behind `/api/status`.
* `deploy/`: Caddy configuration, systemd unit and server setup.

## Running it locally

```bash
npm install
npm run dev          # http://localhost:5173
npm test             # format tests against the shared vectors
npm run check        # type check
npm run build        # static files in dist/
```

The Releases page needs the status service:

```bash
cd server
cargo run            # listens on 127.0.0.1:8787
```

`npm run dev` forwards `/api` to it.

## Deploying

See [deploy/README.md](deploy/README.md).
