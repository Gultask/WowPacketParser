using System;
using System.Collections.Generic;
using System.Linq;
using WowPacketParser.Enums;
using WowPacketParser.Enums.Version;
using WowPacketParser.Parsing;
using WowPacketParser.Parsing.Parsers;

namespace WowPacketParser.Misc
{
    /// <summary>
    /// Drops whole stretches of a sniff before they are parsed, by the map the client was on.
    ///
    /// A capture is a linked list, not an array: records are variable length, so there is no
    /// seeking to packet N without walking to it. What there is instead is a cheap gate. The
    /// map changes a couple of dozen times in a multi-hour capture, and only a handful of
    /// opcodes carry the change, so those are parsed in order as the file is read and every
    /// other packet is stamped with the map in force when it arrived. Packets on an unwanted
    /// map never reach a handler, which is where the time goes.
    ///
    /// Worth it because instance content dominates the volume and yields almost nothing: one
    /// 4.4.1 capture measured 2,648,418 of its 2,842,330 packets - 93.2% - inside Firelands,
    /// a map that does not exist in 3.3.5 at all.
    ///
    /// Settings.MapFilters is a different thing: it drops rows on the way out of the SQL
    /// builders, after every packet has already been parsed, so it saves no time at all.
    /// </summary>
    public static class IngestMapGate
    {
        /// <summary>
        /// Every map in 3.3.5a Map.dbc, read out of a client install rather than typed by hand
        /// (AzerothCore ships map_dbc empty). A map absent from this set cannot hold anything a
        /// WotLK-and-below server can use, so a capture standing on one is pure cost.
        /// </summary>
        private static readonly HashSet<uint> Maps335 = new HashSet<uint>
        {
            0, 1, 13, 25, 30, 33, 34, 35, 36, 37, 42, 43, 44, 47, 48, 70, 90, 109, 129, 169,
            189, 209, 229, 230, 249, 269, 289, 309, 329, 349, 369, 389, 409, 429, 449, 450,
            451, 469, 489, 509, 529, 530, 531, 532, 533, 534, 540, 542, 543, 544, 545, 546,
            547, 548, 550, 552, 553, 554, 555, 556, 557, 558, 559, 560, 562, 564, 565, 566,
            568, 571, 572, 573, 574, 575, 576, 578, 580, 582, 584, 585, 586, 587, 588, 589,
            590, 591, 592, 593, 594, 595, 596, 597, 598, 599, 600, 601, 602, 603, 604, 605,
            606, 607, 608, 609, 610, 612, 613, 614, 615, 616, 617, 618, 619, 620, 621, 622,
            623, 624, 628, 631, 632, 641, 642, 647, 649, 650, 658, 668, 672, 673, 712, 713,
            718, 723, 724
        };

        /// <summary>
        /// The opcodes that move the client between maps. Parsed in file order however the gate
        /// is set, because the gate's own answer depends on them.
        /// </summary>
        private static readonly HashSet<Opcode> MapDefining = new HashSet<Opcode>
        {
            Opcode.SMSG_NEW_WORLD,
            Opcode.SMSG_LOGIN_VERIFY_WORLD,
            Opcode.CMSG_LOADING_SCREEN_NOTIFY
        };

        /// <summary>
        /// Opcodes that must never be gated, because their handler advances the per-connection
        /// zlib stream. Packet.Inflate keeps that stream across packets for builds from 4.3.0
        /// on - which is every branch this gate was written for - so skipping one desynchronises
        /// the stream and quietly corrupts every compressed packet after it, including the ones
        /// on the maps being kept. Always parsing them costs nothing measurable: a 4.4.1 capture
        /// of 2.8M packets contained none of these at all.
        /// </summary>
        private static readonly HashSet<Opcode> StreamBearing = new HashSet<Opcode>
        {
            Opcode.SMSG_COMPRESSED_UPDATE_OBJECT,
            Opcode.SMSG_COMPRESSED_MOVES,
            Opcode.SMSG_MULTIPLE_MOVES,
            Opcode.SMSG_COMPRESSED_MULTIPLE_PACKETS,
            Opcode.SMSG_MULTIPLE_PACKETS,
            Opcode.SMSG_COMPRESSED_ACHIEVEMENT_DATA,
            Opcode.SMSG_COMPRESSED_CHAR_ENUM,
            Opcode.SMSG_COMPRESSED_GUILD_ROSTER,
            Opcode.CMSG_GM_TICKET_CREATE,
            Opcode.SMSG_RESET_COMPRESSION_CONTEXT
        };

        private static HashSet<uint> _allow;
        private static HashSet<uint> _deny = new HashSet<uint>();

        /// <summary>True when packets are being dropped by map.</summary>
        public static bool Enabled { get; private set; }

        /// <summary>The map in force where the reader has reached, or null before the first one.</summary>
        public static uint? CurrentMap { get; private set; }

        /// <summary>Packets seen per map, kept or dropped. uint.MaxValue means no map was known yet.</summary>
        public static Dictionary<uint, int> Census { get; } = new Dictionary<uint, int>();

        /// <summary>Packets never handed to a handler because of the gate.</summary>
        public static int GatedCount { get; private set; }

        /// <summary>Packets that arrived before any map was known, and so could not be gated.</summary>
        public static int UnknownMapCount { get; private set; }

        static IngestMapGate()
        {
            Configure(Settings.IngestMapPolicy, Settings.IngestMapDeny);
        }

        public static void Configure(string policy, IEnumerable<uint> deny)
        {
            _deny = new HashSet<uint>(deny ?? Enumerable.Empty<uint>());

            switch ((policy ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "":
                case "none":
                    _allow = null;
                    break;
                case "wotlk":
                case "335":
                    _allow = Maps335;
                    break;
                default:
                    throw new ArgumentException(
                        "IngestMapPolicy '" + policy + "' is not recognised. Use 'none' or 'wotlk'.");
            }

            Enabled = _allow != null || _deny.Count > 0;
        }

        /// <summary>Forgets the previous file's map and counts.</summary>
        public static void BeginSniff()
        {
            CurrentMap = null;
            GatedCount = 0;
            UnknownMapCount = 0;
            Census.Clear();
        }

        public static bool IsMapWanted(uint map)
        {
            if (_deny.Contains(map))
                return false;
            return _allow == null || _allow.Contains(map);
        }

        /// <summary>
        /// Called for each packet in file order, before it is queued for parsing. Returns true
        /// when the packet should be parsed. A packet carrying a map change is parsed here and
        /// now - single threaded and in order, so the map it publishes is the one the packets
        /// after it actually arrived on - and reported through parsedHere as already handled.
        /// </summary>
        public static bool Admit(Packet packet, out bool parsedHere)
        {
            parsedHere = false;

            var opcode = Opcodes.GetOpcode(packet.Opcode, packet.Direction);

            if (MapDefining.Contains(opcode))
            {
                Handler.Parse(packet);
                parsedHere = true;
                CurrentMap = MovementHandler.CurrentMapId;
                Count(CurrentMap);
                return true;
            }

            Count(CurrentMap);

            if (!Enabled)
                return true;

            if (CurrentMap == null)
            {
                // The login handshake and anything else before the first world packet. None of
                // it belongs to a map and none of it is bulk, so it is always parsed.
                UnknownMapCount++;
                return true;
            }

            if (IsMapWanted(CurrentMap.Value) || StreamBearing.Contains(opcode))
                return true;

            GatedCount++;
            return false;
        }

        private static void Count(uint? map)
        {
            var key = map ?? uint.MaxValue;
            Census.TryGetValue(key, out var n);
            Census[key] = n + 1;
        }
    }
}
