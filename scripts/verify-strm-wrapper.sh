#!/usr/bin/env bash
# verify-strm-wrapper.sh <container> [app]    e.g.  ./verify-strm-wrapper.sh radarr radarr   |   ./verify-strm-wrapper.sh sonarr sonarr
# Run on the Docker host. Verifies init-script execution, binary replacement, mock output (+ <100ms) and passthrough.
C="${1:?container name}"; APP="${2:-$1}"
FF="/app/$APP/bin/ffprobe"
fail=0
ok()  { echo "PASS: $1"; }
bad() { echo "FAIL: $1"; fail=1; }

echo "== 1. Init script ran =="
LOG="$(docker logs "$C" 2>&1 | grep -i -E 'bypass-ffprobe|custom-init')"
echo "$LOG"
echo "$LOG" | grep -q 'wrapper installed' && ok "wrapper installed" || bad "no 'wrapper installed' line"
echo "$LOG" | grep -i -E 'permission denied|not executable|skipp' && bad "permission problem (chmod +x on host?)" || ok "no permission errors"

echo "== 2. Binary replacement =="
docker exec "$C" sh -c "ls -l ${FF} ${FF}.real /usr/bin/ffprobe /usr/bin/ffprobe.real 2>/dev/null"
docker exec "$C" sh -c "grep -q strm-ffprobe-wrapper ${FF}" && ok "$FF is the wrapper" || bad "$FF is not the wrapper"
docker exec "$C" test -x "${FF}.real" && ok "${FF}.real exists and is executable" || bad "${FF}.real missing"

echo "== 3. Mock output on .strm (<100ms) =="
docker exec "$C" sh -c "echo 'https://example.invalid/movie.mkv' > /tmp/dummy.strm"
START=$(date +%s%N)
OUT="$(docker exec "$C" "$FF" -v quiet -print_format json -show_format -show_streams /tmp/dummy.strm)"
END=$(date +%s%N)
MS=$(( (END - START) / 1000000 ))
echo "$OUT" | head -n 12
for pat in '"codec_name": "h264"' '"codec_name": "aac"' '"codec_name": "ac3"' '"channel_layout": "5.1' '"height": 1080'; do
    echo "$OUT" | grep -q "$pat" && ok "found $pat" || bad "missing $pat"
done
# docker exec itself adds ~50-100ms of overhead, so time the wrapper inside the container as well
INNER=$(docker exec "$C" sh -c "s=\$(date +%s%N); $FF /tmp/dummy.strm >/dev/null; e=\$(date +%s%N); echo \$(( (e-s)/1000000 ))")
echo "docker exec round trip: ${MS}ms, in-container: ${INNER}ms"
[ "$INNER" -lt 100 ] && ok "wrapper executed in ${INNER}ms (<100ms)" || bad "wrapper took ${INNER}ms"
docker exec "$C" rm -f /tmp/dummy.strm

echo "== 4. Passthrough for non-.strm =="
docker exec "$C" "$FF" -version | head -n 1 | grep -q '^ffprobe version' && ok "-version handed to real ffprobe" || bad "-version not served by real ffprobe"
ERR="$(docker exec "$C" "$FF" /tmp/does-not-exist.mkv 2>&1)"; echo "$ERR" | tail -n 1
echo "$ERR" | grep -q 'No such file or directory' && ok "real ffprobe reported the missing .mkv" || bad "unexpected passthrough output"

exit $fail
