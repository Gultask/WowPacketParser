#!/usr/bin/env bash
# Rebuild the patrol routes end to end: mine -> chain -> load.
#
# The three steps were run by hand before, which is how the export ended up undocumented.
# Nothing here is incremental; it drops and rebuilds wp_node, wp_edge, path_summary and
# path_point every time. Budget an hour - phase 1 and phase 2 each scan creature_waypoint.
#
#   ./mine-paths.sh [workdir]
#   DB=other-database ./mine-paths.sh
set -euo pipefail

DB=${DB:-wpp_ingest}
MYSQL="mysql -u root -proot --local-infile=1"
WORK="${1:-/c/WowPacketParser/scripts/.paths}"
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$WORK"

# The buffer pool goes back to its 128 MB default whenever the MySQL service restarts, and a
# mine at that size is not slow, it is a different job - phase 0 alone took 57 minutes on
# 2026-09-09 before anyone noticed. Warn rather than change it: an online resize is the
# operator's call and this script does not own the server.
POOL=$($MYSQL -N -e "SELECT @@innodb_buffer_pool_size" 2>/dev/null | tr -dc 0-9)
if [ -n "${POOL:-}" ] && [ "$POOL" -lt 1073741824 ]; then
  echo "WARNING: innodb_buffer_pool_size is $((POOL/1024/1024)) MB. Everything below will crawl."
  echo "         SET GLOBAL innodb_buffer_pool_size = 4294967296;   -- online, reverts on restart"
fi

# SKIP_MINE=1 picks the run up at the export, for when mine-paths.sql already ran - including
# when it was resumed by hand from a middle phase after a failure. The mine is two hours and
# phases 0 and 1 are deterministic, so re-deriving wp_point to redo a five minute export is
# not a price worth paying twice.
if [[ "${SKIP_MINE:-0}" == "1" ]]; then
  echo "== skipping the mine; wp_node and wp_edge are taken as they stand =="
else
  echo "== mining nodes and edges =="
  $MYSQL --table "$DB" < "$HERE/mine-paths.sql"
fi

echo "== exporting =="
# --batch gives a tab separated dump with a header row, which is what chain-paths.py reads.
# node_key is exported under the name the chainer already knows.
# n_obs travels beside n_sniffs now: it is what admits an edge, while n_sniffs stays the
# chainer sort key. Both reach the published path_point.
$MYSQL --batch "$DB" -e \
  "SELECT entry, map, node_key AS pos_key, x, y, z FROM wp_node" > "$WORK/nodes.tsv"
$MYSQL --batch "$DB" -e \
  "SELECT entry, map, from_key, to_key, n_sniffs, spline_obs, n_obs FROM wp_edge" > "$WORK/edges.tsv"
wc -l "$WORK/nodes.tsv" "$WORK/edges.tsv"

echo "== chaining =="
python "$HERE/chain-paths.py" \
  "$WORK/nodes.tsv" "$WORK/edges.tsv" "$WORK/path_summary.tsv" "$WORK/path_point.tsv"

echo "== loading =="
# The mysql client is a Windows binary and cannot resolve an MSYS path like /c/..., so the two
# filenames handed to LOAD DATA are converted while the shell redirects above keep using the MSYS
# form. -m rather than -w: it yields C:/dir/file, which MySQL accepts and which needs none of the
# backslash doubling a Windows path would drag through two layers of string escaping.
# LOAD DATA LOCAL needs the SERVER side enabled as well as the client's --local-infile=1, and
# that global does not survive a mysqld restart - so set it here rather than assuming the box
# is still configured from last time. A 110 minute mine that dies on the load step is a bad way
# to find out.
$MYSQL -e "SET GLOBAL local_infile = 1;"

WIN_SUMMARY=$(cygpath -m "$WORK/path_summary.tsv")
WIN_POINT=$(cygpath -m "$WORK/path_point.tsv")

# Both tables are rebuilt rather than truncated. They used to be created by hand, which is the
# same way the export went undocumented, and a hand-made table cannot grow a column when the
# chainer starts writing one.
#
# IGNORE 1 LINES drops the header the chainer writes.
$MYSQL "$DB" -e "
DROP TABLE IF EXISTS path_summary;
CREATE TABLE path_summary (
  path_id         INT UNSIGNED NOT NULL,
  entry           INT UNSIGNED NOT NULL,
  map             INT UNSIGNED NOT NULL,
  n_points        INT NOT NULL,
  closed          TINYINT NOT NULL,
  close_seq       INT NOT NULL DEFAULT -1 COMMENT 'seq the last point leads back to, -1 if the route does not close',
  length_yd       FLOAT NOT NULL,
  min_edge_sniffs INT NOT NULL,
  max_edge_sniffs INT NOT NULL,
  spline_edges    INT NOT NULL,
  min_edge_obs    INT NOT NULL COMMENT 'total traversals of the weakest edge; n_sniffs=1 with a high n_obs is one capture watching many laps',
  max_edge_obs    INT NOT NULL,
  covered_by      INT UNSIGNED NOT NULL DEFAULT 0 COMMENT 'a longer route walking all of this one\'s ground, 0 if none; same entry means a gap artifact, a different entry means a shared circuit',
  PRIMARY KEY (path_id),
  KEY ix_entry (entry, map),
  KEY ix_conf (min_edge_sniffs, n_points)
) ENGINE=InnoDB COMMENT='routes rebuilt from ordered position pairs walked at least twice';

DROP TABLE IF EXISTS path_point;
CREATE TABLE path_point (
  path_id     INT UNSIGNED NOT NULL,
  entry       INT UNSIGNED NOT NULL,
  map         INT UNSIGNED NOT NULL,
  seq         INT NOT NULL,
  x FLOAT NOT NULL, y FLOAT NOT NULL, z FLOAT NOT NULL,
  edge_sniffs INT NOT NULL,
  edge_obs    INT NOT NULL,
  PRIMARY KEY (path_id, seq),
  KEY ix_pos (entry, map, x, y)
) ENGINE=InnoDB;

LOAD DATA LOCAL INFILE '$WIN_SUMMARY' INTO TABLE path_summary
  FIELDS TERMINATED BY '\t' LINES TERMINATED BY '\n' IGNORE 1 LINES
  (path_id, entry, map, n_points, closed, close_seq, length_yd, min_edge_sniffs, max_edge_sniffs, spline_edges, min_edge_obs, max_edge_obs, covered_by);
LOAD DATA LOCAL INFILE '$WIN_POINT' INTO TABLE path_point
  FIELDS TERMINATED BY '\t' LINES TERMINATED BY '\n' IGNORE 1 LINES
  (path_id, entry, map, seq, x, y, z, edge_sniffs, edge_obs);"

$MYSQL --table "$DB" -e "
SELECT COUNT(*) paths, SUM(closed) closed_loops, SUM(n_points) points,
       ROUND(AVG(n_points),1) avg_points FROM path_summary;
SELECT SUM(s.covered_by > 0) covered,
       SUM(s.covered_by > 0 AND c.entry =  s.entry) gap_artifacts,
       SUM(s.covered_by > 0 AND c.entry <> s.entry) shared_circuits
FROM path_summary s LEFT JOIN path_summary c ON c.path_id = s.covered_by;"

echo "== done. run build-digest.sql to republish sniff_creature_path =="
