#!/bin/bash
cid=$(docker ps -q -f name=command-center)
if [ -n "$cid" ]; then
    docker exec $cid sh -lc 'mkdir -p /app/wwwroot/_framework && blazor_web_js=$(find /usr/share/dotnet -type f -name blazor.web.js 2>/dev/null | sort | tail -n 1) && cp "$blazor_web_js" /app/wwwroot/_framework/blazor.web.js && ls -l /app/wwwroot/_framework/blazor.web.js'
    echo "Done!"
else
    echo "Container not found"
fi
