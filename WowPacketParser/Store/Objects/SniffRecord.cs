using System;

namespace WowPacketParser.Store.Objects
{
    /// <summary>
    /// One row of the ingest `sniff` table: everything we know about a sniff file as a whole.
    /// Every table produced from a sniff hangs off this row's id, so a bad or superseded
    /// sniff can be traced and removed without touching the rest of the pile.
    /// All times are UTC.
    /// </summary>
    public sealed class SniffRecord
    {
        public ulong Id;

        public string FileHash;
        public string FileName;
        public long? FileSize;

        public string Sniffer;
        public int? SnifferId;
        public int? SnifferVersion;
        public string PktVersion;

        public int? ClientBuild;
        public string ClientVersion;
        public string ClientLocale;
        /// <summary>
        /// Content line the sniff came from: Retail, Classic, TBC, WotLK, Cata, MoP.
        /// Recorded instead of the expansion because ClientType reuses numeric values across
        /// branches, so a Burning Crusade Classic sniff would otherwise be filed as Shadowlands.
        /// </summary>
        public string Branch;

        public DateTime? HeaderStartUtc;
        public DateTime? FirstPacketUtc;
        public DateTime? LastPacketUtc;

        /// <summary>Capturing machine's offset from UTC. Null when the sniff header does not record it.</summary>
        public int? UtcOffsetSeconds;

        /// <summary>Where UtcOffsetSeconds came from: "header" or "unknown".</summary>
        public string UtcOffsetSource => UtcOffsetSeconds.HasValue ? "header" : "unknown";

        /// <summary>
        /// First packet time minus header start time, in seconds. Should be a few seconds.
        /// A value near a whole hour means that sniffer wrote local time where UTC was expected,
        /// which matters once sniffs from several contributors are pooled.
        /// </summary>
        public int? ClockSkewSeconds;

        public int? PacketCount;
        public int? ParsedCount;
        public int? ErrorCount;
        public int? SkippedCount;
        public int? NoStructureCount;

        public uint? StructureVersion;
        public DateTime IngestedAtUtc = DateTime.UtcNow;
    }
}
