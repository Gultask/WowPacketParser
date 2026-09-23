using System;
using System.Collections.Generic;
using System.Linq;
using WowPacketParser.Store;
using WowPacketParser.Store.Objects;

namespace WowPacketParser.Loading
{
    public partial class SniffFile
    {
        /// <summary>SMSG_QUERY_GAME_OBJECT_RESPONSE, as the parser stored it.</summary>
        private static List<GameObjectTemplateRecord> CollectGameObjectTemplates(out List<GameObjectQuestItemRecord> questItems)
        {
            var templates = new List<GameObjectTemplateRecord>();
            foreach (var pair in Storage.GameObjectTemplates)
            {
                var t = pair.Item1;
                if (t?.Entry == null || t.Entry == 0)
                    continue;

                templates.Add(new GameObjectTemplateRecord
                {
                    Entry = t.Entry.Value,
                    Type = t.Type == null ? null : (uint?)t.Type,
                    DisplayId = t.DisplayID,
                    Name = t.Name,
                    IconName = t.IconName,
                    CastBarCaption = t.OpeningText,
                    Unk1 = t.ClosingText,
                    Size = t.Size,
                    Data = t.Data == null ? null : string.Join(",", t.Data.Select(d => d ?? 0)),
                    RequiredLevel = t.RequiredLevel,
                    ContentTuningId = t.ContentTuningId
                });
            }

            questItems = new List<GameObjectQuestItemRecord>();
            foreach (var pair in Storage.GameObjectTemplateQuestItems)
            {
                var q = pair.Item1;
                if (q.GameObjectEntry == null || q.GameObjectEntry == 0 || q.ItemId == null || q.ItemId == 0)
                    continue;

                questItems.Add(new GameObjectQuestItemRecord
                {
                    Entry = q.GameObjectEntry.Value,
                    Index = q.Idx ?? 0,
                    ItemId = q.ItemId.Value
                });
            }

            return templates;
        }

        /// <summary>
        /// Trainer lists. Builds before 4.0.6 send no trainer id and are stored by creature entry;
        /// later ones are tied back to the creature through the gossip option that opened the
        /// list, which the parser tracks as it goes - a list opened with no option in front of it
        /// keeps entry 0.
        /// </summary>
        private static List<NpcTrainerRecord> CollectNpcTrainers(out List<TrainerRecord> trainers)
        {
            trainers = new List<TrainerRecord>();
            foreach (var pair in Storage.Trainers)
            {
                var t = pair.Item1;
                if (t?.Id == null)
                    continue;

                trainers.Add(new TrainerRecord
                {
                    TrainerId = t.Id.Value,
                    Type = t.Type == null ? null : (uint?)t.Type,
                    Greeting = t.Greeting
                });
            }

            var spells = new List<NpcTrainerRecord>();
            foreach (var pair in Storage.NpcTrainers)
            {
                var s = pair.Item1;
                if (s?.ID == null || s.SpellID == null || s.SpellID <= 0)
                    continue;

                spells.Add(new NpcTrainerRecord
                {
                    Entry = s.ID.Value,
                    TrainerId = s.ID.Value,
                    SpellId = (uint)s.SpellID.Value,
                    MoneyCost = s.MoneyCost ?? 0,
                    ReqSkillLine = s.ReqSkillLine ?? 0,
                    ReqSkillRank = s.ReqSkillRank ?? 0,
                    ReqLevel = s.ReqLevel ?? 0
                });
            }

            if (spells.Count > 0)
                return spells;

            var entries = new Dictionary<uint, HashSet<uint>>();
            foreach (var pair in Storage.CreatureTrainers)
            {
                var c = pair.Item1;
                if (c?.TrainerID == null || c.CreatureID == null || c.CreatureID == 0)
                    continue;

                if (!entries.TryGetValue(c.TrainerID.Value, out var set))
                    entries[c.TrainerID.Value] = set = new HashSet<uint>();
                set.Add(c.CreatureID.Value);
            }

            foreach (var pair in Storage.TrainerSpells)
            {
                var s = pair.Item1;
                if (s?.TrainerId == null || s.SpellId == null || s.SpellId == 0)
                    continue;

                var trainerId = s.TrainerId.Value;
                var creatures = entries.TryGetValue(trainerId, out var set) ? set : new HashSet<uint> { 0 };
                foreach (var entry in creatures)
                {
                    spells.Add(new NpcTrainerRecord
                    {
                        Entry = entry,
                        TrainerId = trainerId,
                        SpellId = s.SpellId.Value,
                        MoneyCost = s.MoneyCost ?? 0,
                        ReqSkillLine = s.ReqSkillLine ?? 0,
                        ReqSkillRank = s.ReqSkillRank ?? 0,
                        ReqAbility1 = s.ReqAbility?.ElementAtOrDefault(0) ?? 0,
                        ReqAbility2 = s.ReqAbility?.ElementAtOrDefault(1) ?? 0,
                        ReqAbility3 = s.ReqAbility?.ElementAtOrDefault(2) ?? 0,
                        ReqLevel = s.ReqLevel ?? 0
                    });
                }
            }

            return spells;
        }

        /// <summary>
        /// Gossip map pins, with the option that sent each one where the parser saw the option
        /// picked just before.
        /// </summary>
        private static List<GossipPoiRecord> CollectGossipPois()
        {
            var options = new Dictionary<int, (uint Menu, uint Option)>();
            foreach (var pair in Storage.GossipMenuOptions)
            {
                if (pair.Value.Item1.ActionPoiID is int poi && poi != 0 &&
                    pair.Key.Item1 != null && pair.Key.Item2 != null)
                    options[poi] = (pair.Key.Item1.Value, pair.Key.Item2.Value);
            }

            var pois = new List<GossipPoiRecord>();
            foreach (var pair in Storage.GossipPOIs)
            {
                var p = pair.Item1;
                var id = p.ID is int i ? i : 0;
                (uint Menu, uint Option)? option = id != 0 && options.TryGetValue(id, out var o) ? o : null;

                pois.Add(new GossipPoiRecord
                {
                    PoiId = id,
                    MenuId = option?.Menu,
                    OptionIndex = option?.Option,
                    PositionX = p.PositionX,
                    PositionY = p.PositionY,
                    PositionZ = p.PositionZ,
                    Icon = p.Icon == null ? null : (uint?)p.Icon,
                    Flags = p.Flags,
                    Importance = p.Importance,
                    Name = p.Name,
                    WmoGroupId = p.WMOGroupID
                });
            }

            return pois;
        }

        /// <summary>SMSG_QUEST_POI_QUERY_RESPONSE: the blobs, and the outline of each.</summary>
        private static List<QuestPoiRecord> CollectQuestPois(out List<QuestPoiPointRecord> points)
        {
            var pois = new List<QuestPoiRecord>();
            foreach (var pair in Storage.QuestPOIs)
            {
                var p = pair.Item1;
                if (p?.QuestID == null)
                    continue;

                pois.Add(new QuestPoiRecord
                {
                    QuestId = p.QuestID.Value,
                    BlobIndex = p.BlobIndex ?? 0,
                    Idx1 = p.ID ?? 0,
                    ObjectiveIndex = p.ObjectiveIndex,
                    QuestObjectiveId = p.QuestObjectiveID,
                    QuestObjectId = p.QuestObjectID,
                    MapId = p.MapID,
                    UiMapId = p.UiMapID,
                    WorldMapAreaId = p.WorldMapAreaId,
                    Floor = p.Floor,
                    Priority = p.Priority,
                    Flags = p.Flags,
                    WorldEffectId = p.WorldEffectID,
                    PlayerConditionId = p.PlayerConditionID,
                    NavigationPlayerConditionId = p.NavigationPlayerConditionID,
                    SpawnTrackingId = p.SpawnTrackingID,
                    AlwaysAllowMergingBlobs = p.AlwaysAllowMergingBlobs
                });
            }

            points = new List<QuestPoiPointRecord>();
            foreach (var pair in Storage.QuestPOIPoints)
            {
                var p = pair.Item1;
                if (p?.QuestID == null)
                    continue;

                points.Add(new QuestPoiPointRecord
                {
                    QuestId = p.QuestID.Value,
                    Idx1 = p.Idx1 ?? 0,
                    Idx2 = p.Idx2 ?? 0,
                    X = p.X ?? 0,
                    Y = p.Y ?? 0,
                    Z = p.Z ?? 0
                });
            }

            return pois;
        }
    }
}
