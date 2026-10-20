#!/usr/bin/env bash
# Use the image's Bash instead of adding curl/packages to the runtime image.
# /readyz intentionally needs no API key and returns no collection contents.
set -euo pipefail

exec 3<>/dev/tcp/127.0.0.1/6333
printf 'GET /readyz HTTP/1.0\r\nHost: localhost\r\nConnection: close\r\n\r\n' >&3
IFS= read -r response_status <&3
[[ "$response_status" == HTTP/*' 200 '* ]]
