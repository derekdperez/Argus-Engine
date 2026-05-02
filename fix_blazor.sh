#!/bin/bash
set -euo pipefail

cid=$(docker ps -q -f name=command-center | head -n 1)
if [ -n "$cid" ]; then
    docker exec "$cid" sh -lc 'test -s /app/wwwroot/_framework/blazor.web.js && ! grep -q "^404: Not Found" /app/wwwroot/_framework/blazor.web.js && ls -l /app/wwwroot/_framework/blazor.web.js'
    echo "Blazor framework asset is present and is not a 404 payload."
else
    echo "Container not found"
fi
