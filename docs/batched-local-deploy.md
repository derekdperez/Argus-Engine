# Batched local/EC2 deployment helper

This overlay adds a safer deployment entry point:

```bash
./deploy/deploy-batched.sh --image
```

It keeps the existing Argus deployment logic, but:

- disables the Python TUI with `ARGUS_NO_UI=1`;
- uses the existing `argus_BUILD_SEQUENTIAL=1` mode so selected service images are built one at a time;
- forces plain BuildKit output so the real failing service is visible;
- disables Compose Bake unless explicitly opted in;
- adds a per-service timeout through `argus_BUILD_TIMEOUT_MIN`.

## Why this helps

The normal deploy can plan a rebuild of 17 app images. When all changed images are handed to Compose at once, Compose/BuildKit may appear stuck while the host is actually CPU, RAM, disk, or network bound. Sequential mode lets Docker cache successful work per service and makes the first failing image obvious.

## Commands

From the repo root:

```bash
chmod +x deploy/deploy-batched.sh deploy/deploy-preflight.sh

./deploy/deploy-preflight.sh
./deploy/deploy-batched.sh --image
```

If you need a completely fresh rebuild:

```bash
./deploy/deploy-batched.sh -fresh
```

If a cold build needs longer than 45 minutes for one service:

```bash
argus_BUILD_TIMEOUT_MIN=90 ./deploy/deploy-batched.sh --image
```

If Docker requires root on the EC2 instance:

```bash
sudo ./deploy/deploy-batched.sh --image
```

Prefer adding `ec2-user` to the `docker` group later so Azure/AWS/GCP CLI credentials do not split between `ec2-user` and `root`.
