#!/usr/bin/env bash
# Chunked companion to distill-waypoints.sql.
#
# The delete removes tens of millions of rows. As one statement that is a single enormous
# transaction - a huge undo log, a long lock, and no way to see progress or stop it. Looping a
# sniff at a time keeps each transaction small, lets the run be interrupted safely, and prints
# where it got to.
set -u
DB=${DB:-wpp_ingest2}
MYSQL="mysql -uroot -proot $DB --batch --skip-column-names"

total=0
ids=$($MYSQL -e "SELECT DISTINCT sniff_id FROM creature_movement ORDER BY sniff_id;" 2>/dev/null)
n=$(echo "$ids" | wc -l)
i=0
echo "$(date +%H:%M:%S)  distilling across $n sniffs"
for s in $ids; do
  i=$((i+1))
  d=$($MYSQL -e "
    DELETE w FROM creature_waypoint w
    JOIN creature_movement m ON m.sniff_id=w.sniff_id AND m.guid=w.guid
    WHERE w.sniff_id=$s AND m.sniff_id=$s
      AND m.multi_point_segments=0 AND m.radius<30 AND m.points>=40;
    SELECT ROW_COUNT();" 2>/dev/null)
  total=$((total+d))
  if [ $((i % 100)) -eq 0 ]; then
    echo "$(date +%H:%M:%S)  $i/$n sniffs, $total rows removed"
  fi
done
echo "$(date +%H:%M:%S)  done: $total rows removed from creature_waypoint"
echo "reclaim file space with: ALTER TABLE creature_waypoint ENGINE=InnoDB;"
