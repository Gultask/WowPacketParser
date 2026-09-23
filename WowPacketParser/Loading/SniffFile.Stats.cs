using System;
using System.Collections.Generic;
using System.Linq;
using WowPacketParser.Proto;
using WowPacketParser.Store.Objects;

namespace WowPacketParser.Loading
{
    public partial class SniffFile
    {
        /// <summary>
        /// A unit's stat sheet as the server last sent it. Only the unit's owner, charmer or rider
        /// is sent one, so a creature's arrives while the sniffer has it as a pet or guardian,
        /// rides it or mind controls it. Updates carry only what changed, so the sheet is kept
        /// whole here and folded entire each time part of it moves.
        /// </summary>
        private sealed class StatSheet
        {
            public float? MinDamage, MaxDamage, MinOffHandDamage, MaxOffHandDamage, MinRangedDamage, MaxRangedDamage;
            public int? AttackPower, AttackPowerModPos, AttackPowerModNeg, RangedAttackPower;
            public float? AttackPowerMultiplier;
            public int? BaseHealth, BaseMana;
            public readonly int?[] Stats = new int?[5];
            public readonly int?[] StatPosBuff = new int?[5];
            public readonly int?[] StatNegBuff = new int?[5];
            public readonly int?[] Resistances = new int?[7];
            public readonly int?[] ResistancePos = new int?[7];
            public readonly int?[] ResistanceNeg = new int?[7];
        }

        private readonly Dictionary<string, CreatureStatsRecord> _statSheets = new();

        private static bool IsEmptyGuid(UniversalGuid guid) =>
            (guid.Guid128 == null || (guid.Guid128.Low == 0 && guid.Guid128.High == 0)) &&
            (guid.Guid64 == null || (guid.Guid64.Low == 0 && guid.Guid64.High == 0));

        /// <summary>
        /// How the unit is held, as the update stream last said: charmed first, since a charm
        /// overrides whatever summoned it.
        /// </summary>
        private static void TrackRelation(UnitState state, UpdateValuesUnitDataFields unit)
        {
            if (unit.CharmedBy == null && unit.SummonedBy == null && unit.CreatedBy == null && unit.DemonCreator == null)
                return;

            state.Held ??= new string[4];
            void Note(int slot, string name, UniversalGuid g)
            {
                if (g != null)
                    state.Held[slot] = IsEmptyGuid(g) ? null : name;
            }
            Note(0, "charmed", unit.CharmedBy);
            Note(1, "summoned", unit.SummonedBy);
            Note(2, "created", unit.CreatedBy);
            Note(3, "demon", unit.DemonCreator);
            state.Relation = state.Held.FirstOrDefault(h => h != null) ?? "";
        }

        private static void Merge(int?[] sheet, Google.Protobuf.Collections.RepeatedField<Int32ValueWrapper> update)
        {
            for (var i = 0; i < Math.Min(sheet.Length, update.Count); i++)
                sheet[i] = update[i]?.Value ?? sheet[i];
        }

        private void FoldStatSheet(string key, UniversalGuid guid, UnitState state, UpdateValuesUnitDataFields unit)
        {
            // Damage, attack power and stats are never sent to anyone but the owner; base health
            // and mana are, on some builds, so they do not open a sheet by themselves.
            var sent = unit.MinDamage != null || unit.MaxDamage != null || unit.AttackPower != null ||
                       unit.Stats.Count > 0 || unit.StatPosBuff.Count > 0 || unit.MinRangedDamage != null ||
                       unit.ResistanceBuffModsPositive.Count > 0;
            if (!sent && state.Sheet == null)
                return;

            var sheet = state.Sheet ??= new StatSheet();
            sheet.MinDamage = unit.MinDamage ?? sheet.MinDamage;
            sheet.MaxDamage = unit.MaxDamage ?? sheet.MaxDamage;
            sheet.MinOffHandDamage = unit.MinOffHandDamage ?? sheet.MinOffHandDamage;
            sheet.MaxOffHandDamage = unit.MaxOffHandDamage ?? sheet.MaxOffHandDamage;
            sheet.MinRangedDamage = unit.MinRangedDamage ?? sheet.MinRangedDamage;
            sheet.MaxRangedDamage = unit.MaxRangedDamage ?? sheet.MaxRangedDamage;
            sheet.AttackPower = unit.AttackPower ?? sheet.AttackPower;
            sheet.AttackPowerModPos = unit.AttackPowerModPos ?? sheet.AttackPowerModPos;
            sheet.AttackPowerModNeg = unit.AttackPowerModNeg ?? sheet.AttackPowerModNeg;
            sheet.AttackPowerMultiplier = unit.AttackPowerMultiplier ?? sheet.AttackPowerMultiplier;
            sheet.RangedAttackPower = unit.RangedAttackPower ?? sheet.RangedAttackPower;
            sheet.BaseHealth = unit.BaseHealth ?? sheet.BaseHealth;
            sheet.BaseMana = unit.BaseMana ?? sheet.BaseMana;
            Merge(sheet.Stats, unit.Stats);
            Merge(sheet.StatPosBuff, unit.StatPosBuff);
            Merge(sheet.StatNegBuff, unit.StatNegBuff);
            Merge(sheet.Resistances, unit.Resistances);
            Merge(sheet.ResistancePos, unit.ResistanceBuffModsPositive);
            Merge(sheet.ResistanceNeg, unit.ResistanceBuffModsNegative);

            // A health or level tick on a unit the sniffer once rode is not a new sheet: only a
            // sheet the server has just sent is current.
            if (!sent || sheet.MinDamage == null)
                return;

            static string Join(int?[] values) => string.Join(",", values.Select(v => v?.ToString() ?? ""));

            var s = StateOf(key, guid);
            var unitType = guid.Type switch
            {
                UniversalHighGuid.Pet => "pet",
                UniversalHighGuid.Vehicle => "vehicle",
                _ => "creature"
            };

            var row = new CreatureStatsRecord
            {
                Entry = guid.Entry,
                UnitType = unitType,
                Relation = s.Relation,
                Map = s.Map,
                Level = s.Level,
                Class = s.Class,
                Auras = s.AuraKey,
                MaxHealth = s.MaxHealth,
                BaseHealth = sheet.BaseHealth,
                BaseMana = sheet.BaseMana,
                MinDamage = sheet.MinDamage,
                MaxDamage = sheet.MaxDamage,
                MinOffHandDamage = sheet.MinOffHandDamage,
                MaxOffHandDamage = sheet.MaxOffHandDamage,
                MinRangedDamage = sheet.MinRangedDamage,
                MaxRangedDamage = sheet.MaxRangedDamage,
                AttackPower = sheet.AttackPower,
                AttackPowerModPos = sheet.AttackPowerModPos,
                AttackPowerModNeg = sheet.AttackPowerModNeg,
                AttackPowerMultiplier = sheet.AttackPowerMultiplier,
                RangedAttackPower = sheet.RangedAttackPower,
                AttackTime = s.AttackTime0,
                OffHandAttackTime = s.AttackTime1,
                RangedAttackTime = s.RangedAttackTime,
                Armor = sheet.Resistances[0],
                Stats = Join(sheet.Stats),
                StatPosBuff = Join(sheet.StatPosBuff),
                StatNegBuff = Join(sheet.StatNegBuff),
                Resistances = Join(sheet.Resistances),
                ResistancePos = Join(sheet.ResistancePos),
                ResistanceNeg = Join(sheet.ResistanceNeg)
            };

            var id = row.Key();
            if (_statSheets.TryGetValue(id, out var existing))
                row = existing;
            else
                _statSheets[id] = row;

            row.Guids.Add(key);
            row.Updates++;
        }

        private List<CreatureStatsRecord> CollectCreatureStats(ulong sniffId)
        {
            foreach (var row in _statSheets.Values)
                row.SniffId = sniffId;
            return _statSheets.Values.ToList();
        }
    }
}
