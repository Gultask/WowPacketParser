#!/usr/bin/env bash
#
# make-release.sh - cut the published dataset into the module tree, one file per table.
#
# Everything lands in modules/mod-sniff-diff/data/sql/db-world/base/, which is where
# AzerothCore's updater looks. The same nine files are therefore both halves of the release:
#
#   - install the module and the data comes with it, applied on first startup;
#   - or take the folder on its own and load it into any MySQL database, no module, no build.
#
# One file per table is AzerothCore's own convention (data/sql/base/db_world/ holds 309 of
# them), and it buys something real here: the updater hashes each file separately, so re-mining
# just the routes reapplies 16 MiB instead of all 146.
#
# ORDERING. The updater applies files sorted by bare filename, globally (UpdateFetcher.cpp:523).
# `sniff_loot` is a view over sniff_loot_set and sniff_names, so it must be created after both -
# hence sniff_view_loot.sql, because "sniff_v" sorts after "sniff_n" and "sniff_l".
#
# Statements are capped at 64 KB (--net-buffer-length) so these load on a server still using the
# old 4 MB max_allowed_packet default. It costs about 1% in size and removes a whole class of
# "packet too large" reports from people whose my.cnf nobody can see.
#
# Nothing here is hand-edited. Re-running this is the only way to change any of it. Run it after
# build-digest.sql and publish-loot.sql have finished.
#
set -euo pipefail

MYSQL_USER="${MYSQL_USER:-root}"
MYSQL_PASS="${MYSQL_PASS:-root}"
DB="${DB:-acore_world}"
MOD="${MOD:-/c/azerothcore-wotlk/modules/mod-sniff-diff}"

OUTDIR="$MOD/data/sql/db-world/base"
HDRDIR="$MOD/sql/schema/headers"

DUMP=(mysqldump -u "$MYSQL_USER" "-p$MYSQL_PASS"
      --default-character-set=utf8mb4 --no-tablespaces --single-transaction
      --skip-comments --net-buffer-length=65536)

TABLES=(sniff_creature_spawn sniff_creature_path
        sniff_names sniff_loot_set sniff_loot_item
        sniff_gameobject_spawn sniff_gameobject_point_sniffs sniff_reject)

# name:file for every view. The file name is what orders it, and a view must load after the
# tables it reads - "sniff_v..." sorts after every "sniff_<table>". sniff_loot reads
# sniff_loot_set and sniff_names; sniff_gameobject_point reads sniff_gameobject_spawn and
# sniff_gameobject_point_sniffs.
VIEWS=(sniff_loot:sniff_view_loot
       sniff_gameobject_point:sniff_view_gameobject_point)

q() { mysql -u "$MYSQL_USER" "-p$MYSQL_PASS" -N -B "$DB" -e "$1" 2>/dev/null; }

size() { awk -v n="$1" -v s="$2" 'BEGIN {printf "  %-28s %8.1f MiB\n", n, s/1048576}'; }

# mysqldump on Windows terminates its own statements with CRLF while the bulk INSERT lines come
# back LF, so the raw output is a file with two kinds of line ending. Nothing refuses to load it,
# but git would normalise it on commit and the checked-out copy would then hash differently from
# the one written here - and the updater reapplies on a changed hash, so a file would reapply
# itself once for no reason on every fresh clone.
#
# Safe on the data: a raw CR only ever appears as a line terminator, because mysqldump escapes a
# carriage return inside a value as the two characters \r.
lf() { tr -d '\r'; }

# DEFINER is stripped from the view: mysqldump records the account that created it, and nobody
# else's server has a root@localhost prepared to own it. The schema qualifier goes too, so the
# view builds in whatever database it is loaded into.
undefine() {
    sed -e 's/ DEFINER=`[^`]*`@`[^`]*`//g' \
        -e 's/SQL SECURITY DEFINER/SQL SECURITY INVOKER/g' \
        -e "s/\`$DB\`\.//g"
}

# ---------------------------------------------------------------------------------------------
# Gates.
#
# publish-loot.sql prints its checks at the end, but a human reading a screen is not a gate. The
# first is the privacy one, and a release that leaks character guids cannot be recalled once
# anybody has pulled it.
#
# `patrols = 1 AND radius > 0` is NOT a gate, though an earlier schema comment claimed it was.
# The radius of a patroller is its route spread, a real measurement worth keeping; what must
# never happen is that number reaching `wander_distance`, and that guard lives at the write
# sites in cs_sniff.cpp, not in the data.
# ---------------------------------------------------------------------------------------------
echo "== gates =="
GATE=$(q "
SELECT CONCAT_WS(' ',
  CONCAT('player_rows=',   (SELECT COUNT(*) FROM sniff_loot_set WHERE owner_type IN ('Player','ActivePlayer'))),
  CONCAT('over_16_items=', (SELECT COUNT(*) FROM sniff_loot_set WHERE items > 16)),
  CONCAT('bad_close_seq=', (SELECT COUNT(*) FROM sniff_creature_path WHERE close_seq < -1)),
  CONCAT('bad_accuracy=',  (SELECT COUNT(*) FROM sniff_creature_spawn WHERE accuracy NOT IN (0,1,2))));")
echo "  $GATE"
if echo "$GATE" | grep -qv '=0 .*=0 .*=0 .*=0'; then
    echo "FAILED: a publish gate is not zero. Do not ship this." >&2
    exit 1
fi

# The stamp is the CORPUS's last packet, not today. The wall clock meant every re-release
# rewrote all nine files whether or not their data had moved: on the 2026-08-28 run five of
# them - names, loot_set, loot_item, reject and the view - were byte-identical apart from this
# one line, sorted content AND row order, and still cost ~100 MB of fresh blobs in git
# history. Stamping the data's own end date makes an unchanged table reproduce byte for byte,
# so a re-release commits only what actually moved. It is also the more useful fact: a reader
# wants to know how far the evidence runs, not which day somebody typed the command.
STAMP=$(mysql -u "$MYSQL_USER" "-p$MYSQL_PASS" -N -B -e "SELECT DATE(MAX(last_packet_utc)) FROM wpp_ingest.sniff;" 2>/dev/null || echo 'unknown')
SNIFFS=$(mysql -u "$MYSQL_USER" "-p$MYSQL_PASS" -N -B -e \
         "SELECT COUNT(*) FROM wpp_ingest.sniff;" 2>/dev/null || echo '?')

# Every file opens with the same provenance block. It is repeated rather than referenced because
# these files travel one at a time - somebody handed sniff_loot_set.sql on its own still has to
# be told it is Classic-era evidence and not 3.3.5a truth.
banner() {
cat <<HDR
--
-- $1 - GENERATED FILE, do not edit by hand.
--
-- mod-sniff-diff sniffed dataset, $SNIFFS packet captures with evidence up to $STAMP, by
-- scripts/make-release.sh. Applied automatically by AzerothCore's updater when the module is
-- installed, and reapplied whenever this file's hash changes. Loads on its own with
-- \`mysql -u acore -p acore_world < $1\` if you only want the data.
--
-- The table is dropped and rebuilt below, which is what makes reapplying safe.
--
-- EVIDENCE, NOT TRUTH. Every row was observed in a packet capture from a CLASSIC-ERA client,
-- never original 3.3.5a. Rows carry a \`branch\` or a per-client sniff count saying which one;
-- filter on it, because a Burning Crusade observation is not WotLK data. Nothing in AzerothCore
-- reads these tables.
--
-- NOT IN IT. No character names, no player guids, no capture filenames. Captures are
-- identified by hash alone.
--
HDR
}

mkdir -p "$OUTDIR"
rm -f "$OUTDIR"/*.sql
rm -rf "$MOD/sql/optional"

echo "== files =="
for t in "${TABLES[@]}"; do
    out="$OUTDIR/$t.sql"
    {
        banner "$t.sql"
        [ -f "$HDRDIR/$t.sql" ] && cat "$HDRDIR/$t.sql"
        echo
        "${DUMP[@]}" "$DB" "$t"
    } | lf > "$out"
done

# The views. DROP TABLE goes out beside DROP VIEW because dropping a view does not drop a
# table of the same name: sniff_gameobject_point WAS a table before 2026-08-28, and a database
# still holding that one would otherwise refuse the CREATE.
for v in "${VIEWS[@]}"; do
    name=${v%%:*}
    file=${v##*:}
    {
        banner "$file.sql"
        cat "$HDRDIR/$file.sql"
        echo
        echo "DROP VIEW  IF EXISTS \`$name\`;"
        echo "DROP TABLE IF EXISTS \`$name\`;"
        mysql -u "$MYSQL_USER" "-p$MYSQL_PASS" -N -B "$DB" -e "SHOW CREATE VIEW $name" 2>/dev/null \
            | cut -f2 | undefine | sed '$s/$/;/'
    } | lf > "$OUTDIR/$file.sql"
done

for f in "$OUTDIR"/*.sql; do
    size "$(basename "$f")" "$(stat -c%s "$f")"
done
size TOTAL "$(cat "$OUTDIR"/*.sql | wc -c)"

# ---------------------------------------------------------------------------------------------
# Prove they load, in the order the updater will apply them.
#
# Into a scratch schema, never over the source. A release nobody has applied anywhere is a
# release nobody has tested, and three failure modes show up here and nowhere earlier: creates
# that disagree with their data, a view applied before its tables, and a DEFINER the target
# cannot grant.
# ---------------------------------------------------------------------------------------------
echo "== verify =="

# `mysql` already stops at the first error and exits 1. An earlier version of this check still
# printed "loads clean" over a bundle that loaded nothing, because it piped mysql through grep
# and let GREP's exit code stand for the load's. Status is captured before anything is filtered,
# because under `set -e` with `pipefail` grep finding nothing to print is itself a failed
# pipeline and would kill the function before it could reach a `return`.
m() {
    local out status=0
    out=$(mysql -u "$MYSQL_USER" "-p$MYSQL_PASS" "$@" 2>&1) || status=$?
    printf '%s' "$out" | grep -v "Using a password" || true
    return $status
}

m -e "DROP DATABASE IF EXISTS sniff_verify; CREATE DATABASE sniff_verify;"
for f in $(ls "$OUTDIR"/*.sql | sort); do
    if ! m sniff_verify < "$f"; then
        echo "FAILED: $(basename "$f") does not load. Do not ship this." >&2
        m -e "DROP DATABASE IF EXISTS sniff_verify;" || true
        exit 1
    fi
done

m -N -B sniff_verify -e "
SELECT 'creature_spawn',   COUNT(*) FROM sniff_creature_spawn
UNION ALL SELECT 'creature_path',    COUNT(*) FROM sniff_creature_path
UNION ALL SELECT 'names',            COUNT(*) FROM sniff_names
UNION ALL SELECT 'loot_set',         COUNT(*) FROM sniff_loot_set
UNION ALL SELECT 'loot_item',        COUNT(*) FROM sniff_loot_item
UNION ALL SELECT 'gameobject_spawn', COUNT(*) FROM sniff_gameobject_spawn
UNION ALL SELECT 'gameobject_point_sniffs', COUNT(*) FROM sniff_gameobject_point_sniffs
UNION ALL SELECT 'reject',           COUNT(*) FROM sniff_reject
UNION ALL SELECT 'loot (view)',      COUNT(*) FROM sniff_loot
UNION ALL SELECT 'gameobject_point (view)', COUNT(*) FROM sniff_gameobject_point;" | sed 's/^/  /'

EMPTY=$(m -N -B sniff_verify -e "
SELECT ((SELECT COUNT(*) FROM sniff_creature_spawn)   > 0)
     + ((SELECT COUNT(*) FROM sniff_creature_path)    > 0)
     + ((SELECT COUNT(*) FROM sniff_loot_set)         > 0)
     + ((SELECT COUNT(*) FROM sniff_names)            > 0)
     + ((SELECT COUNT(*) FROM sniff_gameobject_spawn) > 0)
     + ((SELECT COUNT(*) FROM sniff_loot)             > 0)
     + ((SELECT COUNT(*) FROM sniff_gameobject_point) > 0);")
if [ "$EMPTY" != "7" ]; then
    echo "FAILED: only $EMPTY of 7 sampled tables came back with rows." >&2
    m -e "DROP DATABASE IF EXISTS sniff_verify;" || true
    exit 1
fi

m -e "DROP DATABASE sniff_verify;"
echo "all $(ls "$OUTDIR"/*.sql | wc -l) files load clean, in updater order"

# --- the download ------------------------------------------------------------------------------
#
# The same nine files, zipped, as one release asset. An AzerothCore install never needs this: the
# module carries the data and the updater applies it. The download is for the other cores, where
# a module is not a thing that exists and these are just tables to load into a database.
#
# Deflate, deliberately, even though it is the largest option. 7-Zip's own format gets this to
# 15.5 MB and LZMA-inside-zip to 16.1, against 20.3 for deflate - but Windows Explorer opens
# neither of the small ones, and nobody should have to install a tool to read an open dataset.
# 7-Zip writes a better deflate stream than PowerShell does (20.3 MB against 23.0), so it is used
# when it is there and Compress-Archive is the fallback.
RELEASEDIR="${RELEASEDIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/release}"
NAME="mod-sniff-diff-data-$(date +%Y%m%d)"
ZIP="$RELEASEDIR/$NAME.zip"
SEVENZIP="${SEVENZIP:-/c/Program Files/7-Zip/7z.exe}"

mkdir -p "$RELEASEDIR"
rm -f "$ZIP"

# Staged into a folder of its own so the archive does not spray nine files across whatever
# directory someone happens to extract it in.
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/$NAME"
cp "$OUTDIR"/*.sql "$STAGE/$NAME/"

WIN_SRC=$(cygpath -w "$STAGE/$NAME")
WIN_ZIP=$(cygpath -w "$ZIP")

if [ -x "$SEVENZIP" ]; then
    (cd "$STAGE" && "$SEVENZIP" a -tzip -mx=9 -bso0 -bsp0 "$WIN_ZIP" "$NAME" >/dev/null)
else
    echo "  7-Zip not found, falling back to Compress-Archive (about 3 MB larger)"
    PS="Compress-Archive -Path '$WIN_SRC' -DestinationPath '$WIN_ZIP'"
    PS="$PS -CompressionLevel Optimal -Force"
    powershell.exe -NoProfile -NonInteractive -Command "$PS" >/dev/null
fi

[ -f "$ZIP" ] || { echo "FAILED: no archive was written to $ZIP" >&2; exit 1; }

# Counted from INSIDE the archive rather than from what was staged for it. A zip that wrote
# without complaint and holds eight tables is the failure worth catching, because this file is
# the only copy the other cores ever see.
if [ -x "$SEVENZIP" ]; then
    ZIPPED=$("$SEVENZIP" l -ba -slt "$WIN_ZIP" | grep -c '^Path = .*\.sql$' || true)
else
    PS="Add-Type -A System.IO.Compression.FileSystem;"
    PS="$PS @([IO.Compression.ZipFile]::OpenRead('$WIN_ZIP').Entries.Name -like '*.sql').Count"
    ZIPPED=$(powershell.exe -NoProfile -NonInteractive -Command "$PS" | tr -dc '0-9')
fi
EXPECTED=$(ls "$OUTDIR"/*.sql | wc -l)
if [ "$ZIPPED" != "$EXPECTED" ]; then
    echo "FAILED: archive holds $ZIPPED of $EXPECTED .sql files. Do not ship this." >&2
    exit 1
fi

size "$(basename "$ZIP")" "$(stat -c%s "$ZIP")"
echo "release asset ready: $ZIP ($ZIPPED tables)"
