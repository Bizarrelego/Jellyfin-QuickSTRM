#!/usr/bin/env bash
# jellyfin-notify.sh - Radarr/Sonarr "Custom Script" connection that tells Jellyfin about a newly imported .strm path.
# Calls POST /Library/Media/Updated (path-targeted; scans only that folder/file), falling back to POST /Library/Refresh (full scan).
#
# Required env (set in the container, or edit the defaults below):
#   JELLYFIN_URL   e.g. http://jellyfin:8096
#   JELLYFIN_KEY   Jellyfin Dashboard -> API Keys
# Optional path translation when *arr and Jellyfin mount the media at different paths:
#   PATH_FROM=/data/media   PATH_TO=/media

JELLYFIN_URL="${JELLYFIN_URL:-http://jellyfin:8096}"
JELLYFIN_KEY="${JELLYFIN_KEY:?set JELLYFIN_KEY}"
PATH_FROM="${PATH_FROM:-}"
PATH_TO="${PATH_TO:-}"

# Radarr and Sonarr export differently named variables; the first non-empty one wins.
EVENT="${radarr_eventtype:-${sonarr_eventtype:-}}"
[ "$EVENT" = "Test" ] && { echo "jellyfin-notify: test event OK"; exit 0; }

# Prefer the imported file's own folder; fall back to the movie/series folder.
TARGET="${radarr_moviefile_path:-${sonarr_episodefile_path:-${radarr_movie_path:-${sonarr_series_path:-}}}}"
[ -n "$TARGET" ] || { echo "jellyfin-notify: no path in environment (event: $EVENT)" >&2; exit 0; }

# Report the folder rather than the file so Jellyfin scans the directory containing the .strm.
case "${TARGET,,}" in *.strm) TARGET="$(dirname "$TARGET")" ;; esac

# Translate *arr path -> Jellyfin path
if [ -n "$PATH_FROM" ]; then
    TARGET="${TARGET/#$PATH_FROM/$PATH_TO}"
fi

# JSON-escape backslashes and quotes
ESC="$(printf '%s' "$TARGET" | sed 's/\\/\\\\/g; s/"/\\"/g')"
BODY="{\"Updates\":[{\"Path\":\"${ESC}\",\"UpdateType\":\"Created\"}]}"

post() { # $1 = endpoint, $2 = optional JSON body
    curl -sS -o /dev/null -w '%{http_code}' --max-time 15 --retry 3 --retry-delay 2 \
        -X POST "${JELLYFIN_URL}$1" \
        -H "X-Emby-Token: ${JELLYFIN_KEY}" \
        -H 'Content-Type: application/json' \
        ${2:+-d "$2"}
}

CODE="$(post /Library/Media/Updated "$BODY")"
if [ "$CODE" = "204" ] || [ "$CODE" = "200" ]; then
    echo "jellyfin-notify: Jellyfin notified for $TARGET (HTTP $CODE)"
    exit 0
fi

echo "jellyfin-notify: Media/Updated returned HTTP $CODE; falling back to full /Library/Refresh" >&2
CODE="$(post /Library/Refresh)"
echo "jellyfin-notify: /Library/Refresh returned HTTP $CODE"
exit 0   # never fail the import because Jellyfin was unreachable
