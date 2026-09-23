namespace WowPacketParser.Store.Objects
{
    /// <summary>A gameobject query response.</summary>
    public sealed class GameObjectTemplateRecord
    {
        public uint Entry;
        public uint? Type;
        public uint? DisplayId;
        public string Name;
        public string IconName;
        public string CastBarCaption;
        public string Unk1;
        public float? Size;
        public string Data;
        public int? RequiredLevel;
        public int? ContentTuningId;
    }

    /// <summary>A quest item a gameobject query response declared.</summary>
    public sealed class GameObjectQuestItemRecord
    {
        public uint Entry;
        public uint Index;
        public uint ItemId;
    }

    /// <summary>The header of a trainer list.</summary>
    public sealed class TrainerRecord
    {
        public uint TrainerId;
        public uint? Type;
        public string Greeting;
    }

    /// <summary>One spell on a trainer list, with the creature that showed it where known.</summary>
    public sealed class NpcTrainerRecord
    {
        public uint Entry;
        public uint TrainerId;
        public uint SpellId;
        public uint MoneyCost;
        public uint ReqSkillLine;
        public uint ReqSkillRank;
        public uint ReqAbility1;
        public uint ReqAbility2;
        public uint ReqAbility3;
        public uint ReqLevel;
    }

    /// <summary>A map pin from SMSG_GOSSIP_POI.</summary>
    public sealed class GossipPoiRecord
    {
        public int PoiId;
        public uint? MenuId;
        public uint? OptionIndex;
        public float? PositionX;
        public float? PositionY;
        public float? PositionZ;
        public uint? Icon;
        public uint? Flags;
        public uint? Importance;
        public string Name;
        public int? WmoGroupId;
    }

    /// <summary>A quest POI blob.</summary>
    public sealed class QuestPoiRecord
    {
        public int QuestId;
        public int BlobIndex;
        public int Idx1;
        public int? ObjectiveIndex;
        public int? QuestObjectiveId;
        public int? QuestObjectId;
        public int? MapId;
        public int? UiMapId;
        public int? WorldMapAreaId;
        public int? Floor;
        public int? Priority;
        public int? Flags;
        public int? WorldEffectId;
        public int? PlayerConditionId;
        public int? NavigationPlayerConditionId;
        public int? SpawnTrackingId;
        public bool? AlwaysAllowMergingBlobs;
    }

    /// <summary>One point of a quest POI blob's outline.</summary>
    public sealed class QuestPoiPointRecord
    {
        public int QuestId;
        public int Idx1;
        public int Idx2;
        public int X;
        public int Y;
        public int Z;
    }
}
