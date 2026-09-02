using WowPacketParser.Enums;
using WowPacketParser.Misc;
using WowPacketParser.SQL;

namespace WowPacketParser.Store.Objects
{
    [DBTableName("creature")]
    public sealed record Creature : IDataModel
    {
        [DBFieldName("guid", true, true)]
        public string GUID;

        [DBFieldName("id")]
        public uint? ID;

        [DBFieldName("map")]
        public uint? Map;

        [DBFieldName("zoneId")]
        public uint? ZoneID;

        [DBFieldName("areaId")]
        public uint? AreaID;

        [DBFieldName("spawnMask", TargetedDatabaseFlag.TillWarlordsOfDraenor)]
        public uint? SpawnMask;

        [DBFieldName("spawnDifficulties", TargetedDatabaseFlag.SinceLegion | TargetedDatabaseFlag.AnyClassic)]
        public string spawnDifficulties;

        [DBFieldName("phaseMask", TargetedDatabaseFlag.TillWrathOfTheLichKing)]
        public uint? PhaseMask;

        [DBFieldName("PhaseId", TargetedDatabaseFlag.SinceCataclysm | TargetedDatabaseFlag.AnyClassic)]
        public string PhaseID;

        [DBFieldName("PhaseGroup", TargetedDatabaseFlag.SinceCataclysm | TargetedDatabaseFlag.AnyClassic)]
        public int? PhaseGroup;

        [DBFieldName("equipment_id")]
        public int EquipmentID;

        [DBFieldName("position_x")]
        public float? PositionX;

        [DBFieldName("position_y")]
        public float? PositionY;

        [DBFieldName("position_z")]
        public float? PositionZ;

        [DBFieldName("orientation")]
        public float? Orientation;

        [DBFieldName("spawntimesecs")]
        public int? SpawnTimeSecs;

        [DBFieldName("wander_distance")]
        public float? WanderDistance;

        [DBFieldName("curHealthPct", TargetedDatabaseFlag.Dragonflight | TargetedDatabaseFlag.WotlkClassic | TargetedDatabaseFlag.CataClassic)]
        public uint? CurHealthPct;

        [DBFieldName("MovementType")]
        public uint? MovementType;

        [DBFieldName("unit_flags3", TargetedDatabaseFlag.SinceLegion | TargetedDatabaseFlag.AnyClassic, false, false, true)]
        public uint? UnitFlags3;

        [DBFieldName("CreateObject")]
        public uint? CreateObject;

        [DBFieldName("VerifiedBuild")]
        public int? VerifiedBuild = ClientVersion.BuildInt;

        [DBFieldName("Comment", false, false, true)]
        public string Comment;
    }
}
