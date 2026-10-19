#!/usr/bin/with-contenv bash
# /custom-cont-init.d/bypass-ffprobe.sh
# Runs at every container start (LinuxServer.io s6-overlay). Installs strm-ffprobe-wrapper.sh as ffprobe.
# Idempotent: the original binary is renamed to ffprobe.real only once; the wrapper is refreshed every start.

WRAPPER_SRC="/strm-ffprobe-wrapper.sh"
MARKER="strm-ffprobe-wrapper"

if [ ! -f "$WRAPPER_SRC" ]; then
    echo "[bypass-ffprobe] $WRAPPER_SRC not mounted; skipping"
    exit 0
fi

# /usr/bin/ffprobe is the documented target; newer LSIO Radarr/Sonarr images ship it under /app/<app>/bin instead, so detect both.
installed=0
for target in /usr/bin/ffprobe /app/radarr/bin/ffprobe /app/sonarr/bin/ffprobe; do
    [ -e "$target" ] || continue

    if ! grep -q "$MARKER" "$target" 2>/dev/null; then
        # target is still the original binary: preserve it
        if [ -e "${target}.real" ]; then
            echo "[bypass-ffprobe] ${target}.real already exists; refusing to overwrite it"
            continue
        fi
        mv "$target" "${target}.real" || { echo "[bypass-ffprobe] could not rename $target"; continue; }
    fi

    cp "$WRAPPER_SRC" "$target" && chmod +x "$target" && installed=$((installed + 1))
    echo "[bypass-ffprobe] wrapper installed at $target (real binary: ${target}.real)"
done

[ "$installed" -gt 0 ] || echo "[bypass-ffprobe] no ffprobe binary found; nothing installed"
exit 0
