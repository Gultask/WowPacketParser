using System;

namespace WowPacketParser.Misc
{
    /// <summary>
    /// Header level facts about a sniff file, recorded once per file at ingest time.
    /// All times are genuine UTC.
    /// </summary>
    public sealed class SniffMetadata
    {
        public int SnifferId;

        public short SnifferVersion;

        public string PktVersion;

        /// <summary>Start time written in the sniff header, or null when the header carries none.</summary>
        public DateTime? HeaderStartTimeUtc;

        /// <summary>
        /// Offset of the capturing machine's local clock from UTC, in seconds.
        /// Only ymir sniffer version 0x0103 and above records it; null means the sniff does not say.
        /// Packet times are UTC regardless - this is kept so a sniff can be related back to
        /// the contributor's wall clock.
        /// </summary>
        public int? UtcTimeOffsetSeconds;

        public string SnifferName
        {
            get
            {
                switch (SnifferId)
                {
                    case 0x15:
                    case 0x16: return "ymir";
                    case 'T':  return "TrinityCore PacketLogger";
                    case 'S':  return "WSTC";
                    case 10:   return "xyla";
                    case 0:    return null;
                    default:   return "unknown (0x" + SnifferId.ToString("X2") + ")";
                }
            }
        }
    }
}
