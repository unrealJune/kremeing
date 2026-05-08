# Kremeing on Kubernetes

Two deployments share one Postgres:

```
                 ┌──────────────┐         ┌──────────────┐
   clients  ───► │ kremeing-api │ ──reads─┤              │
                 │  replicas: 2 │         │   Postgres   │
                 └──────────────┘         │              │
                                          │              │
                 ┌──────────────┐         │              │
                 │ kremeing-    │ ─writes►│              │
                 │  poller      │         │              │
                 │  replicas: 1 │         └──────────────┘
                 │  Recreate    │
                 └──────────────┘
                       │ polls every 5 min
                       ▼
                  api.krispykreme.com
                  site.krispykreme.com
```

The split keeps upstream load constant as the API scales: only one
deployment runs the poller, regardless of how many api replicas there are.

## Apply order

```bash
# 1. Build and push the image (replace registry).
docker build -t your-registry/kremeing:0.2.0 .
docker push    your-registry/kremeing:0.2.0

# 2. Edit 30-api.yaml and 40-poller.yaml — replace `kremeing:latest`
#    with `your-registry/kremeing:0.2.0`. Pin a tag, never `:latest`
#    in real environments.

# 3. Customize the secret.
cp k8s/10-database-secret.example.yaml k8s/10-database-secret.yaml
# Edit k8s/10-database-secret.yaml — replace CHANGEME in BOTH the URL
# and `postgres-password`. They must agree.

# 4. Apply, in order. Numeric prefixes match the desired apply sequence.
kubectl apply -f k8s/00-namespace.yaml
kubectl apply -f k8s/10-database-secret.yaml
kubectl apply -f k8s/20-postgres.yaml         # skip if using managed PG
kubectl apply -f k8s/30-api.yaml
kubectl apply -f k8s/40-poller.yaml
kubectl apply -f k8s/50-ingress.example.yaml  # optional
```

## Switching to managed Postgres

1. Delete `20-postgres.yaml` (or skip applying it).
2. In `10-database-secret.yaml`, set `url` to the managed connection
   string and remove `postgres-password` (no in-cluster Postgres needs it).
3. The API and poller pick up the change with no code modifications —
   `KREMEING_DATABASE_URL` accepts both `postgres://...` URL form and
   Npgsql `Host=...;Database=...` key=value form.

## Probes and startup

- `/health` is the readiness + liveness target on both deployments.
- The poller-only pod still binds port 8080 and serves `/health`, so
  k8s probes succeed there too.
- Cold start runs Discovery (~30s of upstream HTTP). The liveness
  `initialDelaySeconds: 90` accommodates that — don't shorten without
  also batching/short-circuiting Discovery.

## Scaling

- **API**: scale `kremeing-api` replicas freely. Each pod runs its own
  in-memory registry but reads/writes shared Postgres state.
- **Poller**: keep at exactly 1. Two pollers double our outbound
  request volume to Krispy Kreme. `strategy: Recreate` enforces the
  invariant during rollouts.

## Verifying

```bash
kubectl -n kremeing get pods
kubectl -n kremeing logs deploy/kremeing-poller -f       # watch ticks land
kubectl -n kremeing exec -it deploy/kremeing-postgres -- \
    psql -U kremeing -d kremeing -c \
    "SELECT count(*) FROM flip_events;"
kubectl -n kremeing port-forward svc/kremeing-api 8080:80
curl http://localhost:8080/stores/899/hot-light
curl http://localhost:8080/docs       # rendered OpenAPI reference
```
