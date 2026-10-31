#!/bin/sh
# The same receiver and web files run on Linux and macOS; no Unity installation needed.
set -eu
demo_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

if [ -n "${HOLOLENS_PYTHON:-}" ]; then
    exec "$HOLOLENS_PYTHON" -u "$demo_root/server/server.py" --open-browser "$@"
fi
if [ -x "$demo_root/.venv/bin/python" ]; then
    exec "$demo_root/.venv/bin/python" -u "$demo_root/server/server.py" --open-browser "$@"
fi
if command -v python3 >/dev/null 2>&1; then
    exec python3 -u "$demo_root/server/server.py" --open-browser "$@"
fi
printf '%s\n' 'Python 3.11+ is required. Install Python or set HOLOLENS_PYTHON to its executable.' >&2
exit 1
