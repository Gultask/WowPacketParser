using System;

namespace WowPacketParser.Store.Objects
{
    /// <summary>
    /// One creature's whole movement history in one sniff, reduced to the numbers the digest
    /// actually needs.
    ///
    /// This exists because the alternative is aggregating tens of millions of waypoint rows
    /// afterwards, which is slow enough to be unusable - a single GROUP BY over the waypoint
    /// table by (sniff, guid) ran for over fifteen minutes and had to be abandoned. Everything
    /// here is computed while the points are already in memory, for free.
    ///
    /// The centre is stored as a median rather than a mean because the median measured better
    /// against known spawn points: 1.88 yd typical error against 2.42 for the mean and 2.66 for
    /// the bounding box midpoint.
    /// </summary>
    public sealed class CreatureMovementRecord
    {
        public ulong SniffId;

        public string Guid;
        public uint Entry;
        public uint Map;

        public int Points;
        public int Segments;

        /// <summary>
        /// Segments carrying more than one point - authored splines, meaning patrol and flight
        /// paths. Measured at 1.4% of segments; the other 98.6% are single random destinations,
        /// which makes this the cheapest random-versus-path discriminator available.
        /// </summary>
        public int MultiPointSegments;

        /// <summary>
        /// Transitions where the creature had clearly finished moving before the next order
        /// arrived. Random movement pauses between destinations while an authored path only
        /// pauses for roleplay or at its ends, so this is a secondary signal for the same
        /// question - weaker than segment length on its own, but independent of it.
        /// </summary>
        public int Transitions;
        public int Pauses;

        public float MedianX;
        public float MedianY;
        public float MedianZ;

        /// <summary>Furthest destination from the median. The wander radius estimate.</summary>
        public float Radius;

        /// <summary>
        /// Same, but at the 99th percentile, so one pull-and-return excursion cannot define it.
        /// Against this robust radius the estimate is 99.9% converged after 40 destinations;
        /// against the raw maximum it takes 120-200.
        /// </summary>
        public float RadiusRobust;

        public DateTime? FirstSeenUtc;
        public DateTime? LastSeenUtc;
    }
}
