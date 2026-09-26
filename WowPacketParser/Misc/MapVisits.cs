using System;
using System.Collections.Generic;

namespace WowPacketParser.Misc
{
    /// <summary>
    /// One stay on a map: from the packet that put the client there to the next one that moved it.
    /// </summary>
    public sealed class MapVisit
    {
        /// <summary>Ordinal within the sniff, from 0.</summary>
        public int Index;

        /// <summary>The packet that began the stay. Everything from here to the next stay's is in it.</summary>
        public int FirstPacket;

        /// <summary>login, new_world, difficulty_change, or sniff_start for what came before any of them.</summary>
        public string StartedBy;

        public uint? Map;

        /// <summary>
        /// The DifficultyID of SMSG_WORLD_SERVER_INFO, which states the instance the client is
        /// in rather than the difficulty it has selected. Null until one arrives, and for good
        /// when none does: a sniff that starts inside an instance, or a build whose handler does
        /// not read the field.
        /// </summary>
        public uint? Difficulty;

        public DateTime? FirstUtc;
        public DateTime? LastUtc;
        public int Packets;

        /// <summary>
        /// The difficulty of a thing on <paramref name="map"/> seen during this stay. Refused when
        /// the stay is known to be on another map - that is a creature this stay never held, and
        /// no answer is better than the wrong one.
        /// </summary>
        public uint? DifficultyFor(uint map) => Map == null || Map == map ? Difficulty : null;
    }

    /// <summary>
    /// Which difficulty every packet of a sniff arrived in.
    ///
    /// The server states it in SMSG_WORLD_SERVER_INFO after every login and world change, and
    /// never in the same packet as the map. Nor first: after SMSG_NEW_WORLD the initial
    /// SMSG_UPDATE_OBJECT - the whole room the player lands in, 41 to 183 creatures in the
    /// captures checked - comes before it. So "the difficulty last announced" is wrong for exactly
    /// that batch; what holds is "the difficulty of the stay this packet belongs to", which is
    /// only known once the stay's SMSG_WORLD_SERVER_INFO has been read. Stays are recorded here
    /// in file order by the reader, and looked up by packet number once the file is done.
    ///
    /// Login is the exception to "after": the server may send it just before
    /// SMSG_LOGIN_VERIFY_WORLD rather than after. One that arrived with no update packet since is
    /// taken to name the world being logged into - see <see cref="Begin"/>.
    ///
    /// Fed by <see cref="IngestMapGate"/>, which already parses the world changes in order.
    /// </summary>
    public static class MapVisits
    {
        private static readonly List<MapVisit> _visits = new();
        private static readonly object _lock = new();

        /// <summary>
        /// The last SMSG_WORLD_SERVER_INFO while no update packet has followed it, and what it did:
        /// set the current stay's difficulty, opened a stay of its own, or neither.
        /// </summary>
        private static (int Packet, DateTime Time, uint Difficulty, bool SetCurrent, bool Opened)? _unanswered;

        /// <summary>Every stay so far, in order. Safe to read once the reader has finished.</summary>
        public static IReadOnlyList<MapVisit> Visits => _visits;

        /// <summary>Forgets the previous file's stays and opens the one before any world packet.</summary>
        public static void BeginSniff()
        {
            lock (_lock)
            {
                _visits.Clear();
                _visits.Add(new MapVisit { Index = 0, FirstPacket = int.MinValue, StartedBy = "sniff_start" });
                _unanswered = null;
            }
        }

        private static MapVisit Current => _visits[^1];

        /// <summary>
        /// A world change: SMSG_NEW_WORLD or SMSG_LOGIN_VERIFY_WORLD. <paramref name="map"/> is null
        /// when the packet did not parse.
        /// </summary>
        public static void Begin(Packet packet, uint? map, string startedBy)
        {
            lock (_lock)
            {
                var visit = new MapVisit
                {
                    FirstPacket = packet.Number,
                    StartedBy = startedBy,
                    Map = map,
                    FirstUtc = packet.Time
                };

                // At login the server can state the difficulty first: 2.5.4 and 10.0.7 captures both
                // send it a few dozen packets ahead of SMSG_LOGIN_VERIFY_WORLD, with nothing created
                // in between. It names the world being logged into, not the one before it, so it is
                // taken back from where it landed and the new stay starts at it.
                if (startedBy == "login" && _unanswered is { } early)
                {
                    // Its packets go with it, so first_packet and packets still partition the file.
                    var moved = Math.Min(packet.Number - early.Packet, Current.Packets);
                    if (early.Opened)
                        _visits.RemoveAt(_visits.Count - 1);
                    else
                    {
                        if (early.SetCurrent)
                            Current.Difficulty = null;
                        Current.Packets -= moved;
                    }

                    visit.FirstPacket = early.Packet;
                    visit.FirstUtc = early.Time;
                    visit.Difficulty = early.Difficulty;
                    visit.Packets = moved;
                }

                _unanswered = null;

                // Nothing before the first world packet belongs anywhere; let the stay it opened
                // start here instead of leaving an empty one behind.
                if (_visits.Count > 0 && Current.StartedBy == "sniff_start" && Current.Packets == 0)
                    _visits.RemoveAt(_visits.Count - 1);

                visit.Index = _visits.Count;
                _visits.Add(visit);
            }
        }

        /// <summary>An update packet: anything that can create an object ends the login lookback.</summary>
        public static void NoteUpdate()
        {
            lock (_lock)
                _unanswered = null;
        }

        /// <summary>
        /// SMSG_WORLD_SERVER_INFO. The first one of a stay names its difficulty. A later one that
        /// disagrees means the instance changed under the client without a world packet, and
        /// starts a stay of its own rather than relabelling what came before.
        /// </summary>
        public static void NoteDifficulty(Packet packet, uint? difficulty, uint? map)
        {
            if (difficulty == null)
                return;

            lock (_lock)
            {
                var current = Current;
                if (current.Difficulty == null)
                {
                    current.Difficulty = difficulty;
                    _unanswered = (packet.Number, packet.Time, difficulty.Value, true, false);
                    return;
                }

                if (current.Difficulty == difficulty)
                {
                    _unanswered = (packet.Number, packet.Time, difficulty.Value, false, false);
                    return;
                }

                _visits.Add(new MapVisit
                {
                    Index = _visits.Count,
                    FirstPacket = packet.Number,
                    StartedBy = "difficulty_change",
                    Map = current.Map ?? map,
                    Difficulty = difficulty,
                    FirstUtc = packet.Time
                });
                _unanswered = (packet.Number, packet.Time, difficulty.Value, false, true);
            }
        }

        /// <summary>Every packet, in file order, so each stay knows its extent.</summary>
        public static void Count(Packet packet, uint? map)
        {
            lock (_lock)
            {
                var current = Current;
                current.Packets++;
                current.FirstUtc ??= packet.Time;
                current.LastUtc = packet.Time;

                // A sniff that starts mid-session learns its map only from the first packet that
                // carries one.
                current.Map ??= map;
            }
        }

        /// <summary>The stay the packet numbered <paramref name="packetNumber"/> arrived in.</summary>
        public static MapVisit VisitAt(int packetNumber)
        {
            lock (_lock)
            {
                if (_visits.Count == 0)
                    return null;

                // Last stay starting at or before the packet.
                int lo = 0, hi = _visits.Count - 1;
                while (lo < hi)
                {
                    var mid = (lo + hi + 1) / 2;
                    if (_visits[mid].FirstPacket <= packetNumber)
                        lo = mid;
                    else
                        hi = mid - 1;
                }

                return _visits[lo];
            }
        }

        /// <summary>
        /// The difficulty of something on <paramref name="map"/> that arrived in packet
        /// <paramref name="packetNumber"/>. Only final once the whole file has been read.
        /// </summary>
        public static uint? DifficultyAt(int packetNumber, uint map) => VisitAt(packetNumber)?.DifficultyFor(map);
    }
}
