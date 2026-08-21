using System;

namespace WowPacketParser.Store.Objects
{
    /// <summary>
    /// One observed gameobject instance, as seen in one sniff.
    /// A gathering node keeps its guid for as long as it stands and gets a new one when it
    /// respawns, so counting distinct rows per position is what gives the odds of a spawn
    /// point rolling one entry over another - counting create packets would just measure
    /// how often the player walked back into range.
    /// </summary>
    public sealed class GameObjectSpawnRecord
    {
        public ulong SniffId;

        public string Guid;
        public uint Entry;
        public uint Map;
        public int? AreaId;
        public int? ZoneId;

        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float Orientation;

        // AzerothCore's gameobject table stores orientation and a quaternion; both matter for
        // placement. GameObject.GetStaticRotation() handles all three encodings the client has
        // used (3.1+ movement rotation, 3.0.2 packed quaternion, and the pre-3.0 float array).
        public float? Rotation0;
        public float? Rotation1;
        public float? Rotation2;
        public float? Rotation3;

        /// <summary>1 = entered visibility range, 2 = created in the world while watching.</summary>
        public int CreateType;

        public uint? PhaseMask;
        public string Phases;

        public DateTime? FirstSeenUtc;
    }
}
