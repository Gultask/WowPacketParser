using System;

namespace WowPacketParser.Store.Objects
{
    /// <summary>
    /// One point of one movement segment, for a creature that was not in combat.
    ///
    /// Only real waypoints are stored. SMSG_ON_MONSTER_MOVE carries two arrays: <c>points</c>,
    /// which the server authored, and <c>packedPoints</c>, which is delta compressed pathfinding
    /// filler between them. Only the first is kept - storing filler as waypoints would bury the
    /// real path in noise. Splines that arrived inside a CreateObject block come through the same
    /// message with <c>creationSpline</c> set, which is how a flying creature's whole path
    /// arrives in one packet.
    ///
    /// Segments are dropped once their creature has aggroed anywhere in the sniff, so chase and
    /// evade movement never reaches the table.
    /// </summary>
    public sealed class CreatureWaypointRecord
    {
        public ulong SniffId;

        public string Guid;
        public uint Entry;
        public uint Map;

        /// <summary>Groups the points that arrived in one packet; ordered within the sniff.</summary>
        public int SegmentId;
        public int PointIndex;

        /// <summary>
        /// How many points this segment carried, repeated on each of its rows. Redundant, but it
        /// turns the random-versus-path split into an indexed filter rather than an aggregation
        /// over the whole table. 98.6% of segments carry exactly one point - a single random
        /// destination - and the rest are authored splines.
        /// </summary>
        public int SegmentPoints;

        public float PositionX;
        public float PositionY;
        public float PositionZ;

        /// <summary>Set on the last point of a segment when the packet carried a facing.</summary>
        public float? Orientation;

        /// <summary>Raw UniversalSplineFlag, so exact/flying/cyclic can be told apart at query time.</summary>
        public uint SplineFlags;

        /// <summary>True when the spline arrived inside a CreateObject block rather than a move packet.</summary>
        public bool CreationSpline;

        /// <summary>Server's stated travel time for the whole segment, in milliseconds.</summary>
        public uint? MoveTimeMs;

        public DateTime? SeenUtc;
    }
}
