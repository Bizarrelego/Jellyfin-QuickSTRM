#!/usr/bin/env bash
# strm-ffprobe-wrapper.sh
# Drop-in ffprobe replacement for LinuxServer.io Radarr/Sonarr containers.
# For .strm inputs it instantly prints mock ffprobe JSON (so the *arr never downloads the remote file);
# for everything else it execs the real ffprobe (renamed to ffprobe.real by custom-cont-init.d/bypass-ffprobe.sh).

# The real binary sits next to this wrapper (/usr/bin/ffprobe.real by default); REAL_FFPROBE overrides.
SELF_DIR="$(dirname "$(readlink -f "$0" 2>/dev/null || echo "$0")")"
REAL_FFPROBE="${REAL_FFPROBE:-$SELF_DIR/ffprobe.real}"
[ -x "$REAL_FFPROBE" ] || REAL_FFPROBE=/usr/bin/ffprobe.real

# Find the .strm input among the arguments (last matching arg wins; ffprobe takes the file last).
STRM=""
for arg in "$@"; do
    case "${arg,,}" in
        *.strm) STRM="$arg" ;;
    esac
done
STRM="${STRM#file:}"

if [ -z "$STRM" ] || [ ! -f "$STRM" ]; then
    exec "$REAL_FFPROBE" "$@"
fi

# Parse the first non-empty line (the URL) - validated only for presence, never fetched.
URL="$(grep -m1 -v '^[[:space:]]*$' "$STRM" | tr -d '\r' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
[ -n "$URL" ] || { echo "strm-ffprobe-wrapper: empty .strm file: $STRM" >&2; exit 1; }

# JSON-escape the URL for the format.filename field.
ESC_URL="$(printf '%s' "$URL" | sed 's/\\/\\\\/g; s/"/\\"/g')"

# Spoofs H.264/AAC output to satisfy Radarr/Sonarr MediaInfo validation, preventing the *arr suite from downloading cloud files.
# This does not affect Jellyfin playback or Astra client transcoding, which strictly rely on Jellyfin's internal probe.
cat <<EOF
{
    "streams": [
        {
            "index": 0,
            "codec_name": "h264",
            "codec_long_name": "H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10",
            "profile": "High",
            "codec_type": "video",
            "width": 1920,
            "height": 1080,
            "coded_width": 1920,
            "coded_height": 1080,
            "pix_fmt": "yuv420p",
            "level": 41,
            "r_frame_rate": "24000/1001",
            "avg_frame_rate": "24000/1001",
            "time_base": "1/1000",
            "bits_per_raw_sample": "8",
            "disposition": { "default": 1 }
        },
        {
            "index": 1,
            "codec_name": "aac",
            "codec_long_name": "AAC (Advanced Audio Coding)",
            "profile": "LC",
            "codec_type": "audio",
            "sample_fmt": "fltp",
            "sample_rate": "48000",
            "channels": 6,
            "channel_layout": "5.1",
            "time_base": "1/1000",
            "disposition": { "default": 1 },
            "tags": { "language": "eng" }
        },
        {
            "index": 2,
            "codec_name": "ac3",
            "codec_long_name": "ATSC A/52A (AC-3)",
            "codec_type": "audio",
            "sample_fmt": "fltp",
            "sample_rate": "48000",
            "channels": 6,
            "channel_layout": "5.1(side)",
            "time_base": "1/1000",
            "disposition": { "default": 0 },
            "tags": { "language": "eng" }
        }
    ],
    "format": {
        "filename": "${ESC_URL}",
        "nb_streams": 3,
        "format_name": "matroska,webm",
        "format_long_name": "Matroska / WebM",
        "start_time": "0.000000",
        "duration": "7200.000000",
        "size": "8000000000",
        "bit_rate": "8888888"
    },
    "chapters": []
}
EOF
exit 0
