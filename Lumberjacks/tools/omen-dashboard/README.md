# OMEN live GCP dashboard

This loopback-only proxy lets OMEN view the live GCP P7 dashboards without adding
public HTTPS or exposing the Valheim control endpoints. It forwards only the HTML
surfaces and `GET /api/v0/telemetry/*`.

From this directory:

```powershell
docker compose up -d
Start-Process http://127.0.0.1:8080/community
```

Other local surfaces:

```text
http://127.0.0.1:8080/roadmap
http://127.0.0.1:8080/networksense
http://127.0.0.1:8080/events
http://127.0.0.1:8080/testing
```

The roadmap is generated from `docs/roadmap/`, carries the append-only implementation
journal, and is mounted directly into this loopback proxy. It updates on the next browser
refresh after `npm run roadmap:render`; no Gateway or GCP redeployment is needed. The other
surfaces proxy the live GCP Gateway on `8.231.129.249:42317`.

The proxy binds only to `127.0.0.1`. `/valheim/*`, Operator API routes, WebSocket
control routes, and non-GET telemetry requests are deliberately unavailable through it.
To point the live surfaces at another GCP Gateway, edit the `upstream` address in
`nginx.conf`.
