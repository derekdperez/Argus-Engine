# Argus Command Center Web compile fix

This overlay replaces `src/ArgusEngine.CommandCenter.Web/Program.cs`.

It fixes the local Docker build failure in `command-center-web` by:

- Removing the stale `DiscoveryRealtimeClient` registration.
- Registering the existing `RealtimeApiClient` typed client instead.
- Registering all Command Center Web API typed clients with the correct `(IServiceProvider, HttpClient)` lambda overload.
- Registering Radzen services directly instead of relying on `AddRadzenComponents()` being resolved by the compiler in this project.

Apply from the repository root:

```bash
unzip -o argus-commandcenter-web-compile-fix.zip
git diff src/ArgusEngine.CommandCenter.Web/Program.cs
```

Fast compile test:

```bash
docker compose -f deploy/docker-compose.yml build --progress=plain command-center-web || sudo docker compose -f deploy/docker-compose.yml build --progress=plain command-center-web
```

Then run the local one-machine deploy:

```bash
ARGUS_NO_UI=1 \
argus_DEPLOY_MODE=image \
argus_BUILD_SEQUENTIAL=1 \
argus_BUILD_PROGRESS=plain \
BUILDKIT_PROGRESS=plain \
COMPOSE_BAKE=false \
COMPOSE_PARALLEL_LIMIT=2 \
argus_BUILD_TIMEOUT_MIN=0 \
bash deploy/deploy.sh --image
```
