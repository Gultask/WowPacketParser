using System;
using System.Collections.Generic;
using System.Linq;
using WowPacketParser.Enums;
using WowPacketParser.Misc;
using WowPacketParser.Proto;
using WowPacketParser.Store;
using WowPacketParser.Store.Objects;

namespace WowPacketParser.Loading
{
    public partial class SniffFile
    {
        /// <summary>
        /// Level, armor, attack time and auras as they stood when a swing landed. Read from update
        /// and aura packets in packet order on the write stage, because the final Storage state is
        /// the wrong one: a levelling capture ends at a different level than it started, armor moves
        /// with gear, forms and buffs, and an Enrage is on for only part of a fight.
        /// </summary>
        private sealed class UnitState
        {
            public uint Level;
            public int Armor = -1;
            public uint AttackTime0;
            public uint AttackTime1;
            public uint RangedAttackTime;
            public uint Class;
            public long MaxHealth;
            public string Relation = "";
            public string[] Held;
            public StatSheet Sheet;
            public readonly Dictionary<int, uint> Auras = new();
            public string AuraKey = "";

            // Constant for the unit's life, so read from Storage once.
            public bool Resolved;
            public uint Map;
            public MapVisit Visit;
            public uint Zone;
            public string Owner = "";
        }

        private readonly Dictionary<string, UnitState> _unitStates = new();

        // Folded per stay rather than per difficulty: a stay's difficulty can arrive after the
        // first swings in it are folded. ByDifficulty merges the stays once the file is read.
        private readonly Dictionary<(uint, uint, MapVisit, string, uint, uint, string, string, string, uint, bool, int), CreatureMeleeRecord>
            _melee = new();

        private readonly Dictionary<(uint, uint, MapVisit, uint, int, string, string, uint), CreatureArmorRecord> _armor = new();

        private const uint HitMiss = 0x10;
        private const uint HitFullAbsorb = 0x20;
        private const uint HitPartialAbsorb = 0x40;
        private const uint HitFullResist = 0x80;
        private const uint HitPartialResist = 0x100;
        private const uint HitCrit = 0x200;
        private const uint HitOffhand = 0x4;
        private const uint HitGlancing = 0x10000;
        private const uint HitCrushing = 0x20000;

        /// <summary>Longest aura list kept whole; a raid boss under thirty debuffs can pass it.</summary>
        private const int MaxAuraKey = 500;

        private UnitState Tracked(string key)
        {
            if (!_unitStates.TryGetValue(key, out var state))
                _unitStates[key] = state = new UnitState();
            return state;
        }

        private void TrackUnitStates(PacketUpdateObject update)
        {
            foreach (var created in update.Created)
                TrackUnitState(created.Guid, created.Values);

            foreach (var updated in update.Updated)
                TrackUnitState(updated.Guid, updated.Values);
        }

        private void TrackUnitState(UniversalGuid guid, UpdateValues values)
        {
            var unit = values?.Fields?.Unit;
            var key = GuidKey(guid);
            if (unit == null || key == null)
                return;

            var state = Tracked(key);
            if (unit.Level > 0)
                state.Level = (uint)unit.Level.Value;
            if (unit.Resistances.Count > 0 && unit.Resistances[0]?.Value != null)
            {
                state.Armor = (int)unit.Resistances[0].Value.Value;
                NoteOwnArmor(guid, key);
            }
            if (unit.AttackRoundBaseTime.Count > 0 && unit.AttackRoundBaseTime[0]?.Value > 0)
                state.AttackTime0 = unit.AttackRoundBaseTime[0].Value.Value;
            if (unit.AttackRoundBaseTime.Count > 1 && unit.AttackRoundBaseTime[1]?.Value > 0)
                state.AttackTime1 = unit.AttackRoundBaseTime[1].Value.Value;
            if (unit.RangedAttackRoundBaseTime > 0)
                state.RangedAttackTime = unit.RangedAttackRoundBaseTime.Value;
            if (unit.ClassId != null)
                state.Class = unit.ClassId.Value;
            if (unit.MaxHealth != null)
                state.MaxHealth = unit.MaxHealth.Value;
            TrackRelation(state, unit);

            if (guid.Type is UniversalHighGuid.Creature or UniversalHighGuid.Vehicle or UniversalHighGuid.Pet)
                FoldStatSheet(key, guid, state, unit);
        }

        private void TrackAuras(PacketAuraUpdate update)
        {
            var key = GuidKey(update.Unit);
            if (key == null)
                return;

            var state = Tracked(key);
            if (update.UpdateAll)
                state.Auras.Clear();

            foreach (var entry in update.Updates)
            {
                if (entry.Remove)
                    state.Auras.Remove(entry.Slot);
                else
                    state.Auras[entry.Slot] = entry.Spell;
            }

            var auraKey = string.Join(",", state.Auras.Values.Where(s => s != 0).Distinct().OrderBy(s => s));
            state.AuraKey = auraKey.Length <= MaxAuraKey
                ? auraKey
                : auraKey.Substring(0, auraKey.LastIndexOf(',', MaxAuraKey - 1)) + ",+";
        }

        /// <summary>
        /// The tracked state, topped up from Storage where the update stream never said.
        ///
        /// A unit whose create came before the capture started never had its values sent whole.
        /// (Until protobuf structure 27 a create block carrying a spline also lost its packet's
        /// UpdateObject - 17% of a WotLK Classic capture's update packets.) Storage is read ahead
        /// of the write stage by the parse threads, which is harmless for level and attack time; armor is left unknown instead, since Storage cannot tell a
        /// creature's unsent armor from a real zero.
        /// </summary>
        private UnitState StateOf(string key, UniversalGuid guid)
        {
            var state = Tracked(key);
            if (state.Resolved && state.Level != 0 && state.AttackTime0 != 0 && state.Class != 0)
                return state;

            WowGuid wowGuid = guid.Guid128 != null ? new WowGuid128(guid.Guid128.Low, guid.Guid128.High)
                            : guid.Guid64 != null ? new WowGuid64(guid.Guid64.Low)
                            : null;
            if (wowGuid == null || !Storage.Objects.TryGetValue(wowGuid, out WoWObject obj))
                return state;

            if (!state.Resolved)
            {
                state.Resolved = true;
                state.Map = obj.Map;
                state.Visit = MapVisits.VisitAt(obj.PacketNumber);
                state.Zone = (uint)Math.Max(obj.Zone, 0);
                if (obj is Unit owned)
                    state.Owner = OwnerType(owned);
            }

            var data = (obj as Unit)?.UnitData;
            if (data == null)
                return state;

            if (state.Level == 0 && data.Level > 0)
                state.Level = (uint)data.Level.Value;
            if (state.AttackTime0 == 0 && data.AttackRoundBaseTime?.Length > 0)
                state.AttackTime0 = data.AttackRoundBaseTime[0] ?? 0;
            if (state.AttackTime1 == 0 && data.AttackRoundBaseTime?.Length > 1)
                state.AttackTime1 = data.AttackRoundBaseTime[1] ?? 0;
            if (state.Class == 0 && data.ClassId > 0)
                state.Class = data.ClassId.Value;

            return state;
        }

        /// <summary>
        /// Whose creature it is. A player's summon hits with its owner's stats behind it, so its
        /// swings say nothing about its template; a boss's add is still the template's own.
        /// </summary>
        private static string OwnerType(Unit unit)
        {
            var d = unit.UnitData;
            var owner = new[] { d?.SummonedBy, d?.CreatedBy, d?.DemonCreator, d?.CharmedBy }
                .FirstOrDefault(g => g != null && !g.IsEmpty());
            if (owner == null)
                return d?.CreatedBySpell > 0 ? "spell" : "";

            return owner.GetHighType() switch
            {
                HighGuidType.Player => "player",
                HighGuidType.Pet => "pet",
                _ => "creature"
            };
        }

        private static string UnitType(UniversalGuid guid) => guid.Type switch
        {
            UniversalHighGuid.Player => "player",
            UniversalHighGuid.Pet => "pet",
            UniversalHighGuid.Creature or UniversalHighGuid.Vehicle => "creature",
            _ => "other"
        };

        private void FoldMelee(PacketHolder holder)
        {
            var swing = holder.AttackerStateUpdate;
            var attacker = GuidKey(swing.Attacker);
            var victim = GuidKey(swing.Victim);
            if (attacker == null || victim == null)
                return;

            var a = StateOf(attacker, swing.Attacker);
            var v = StateOf(victim, swing.Victim);
            var attackerType = UnitType(swing.Attacker);
            var when = holder.BaseData?.Time?.ToDateTime();

            var hit = swing.HitInfo;
            var offhand = (hit & HitOffhand) != 0;
            string kind;
            if ((hit & HitMiss) != 0)
                kind = "miss";
            else
            {
                kind = swing.VictimState switch
                {
                    2 => "dodge",
                    3 => "parry",
                    6 => "evade",
                    7 => "immune",
                    8 => "deflect",
                    _ => (hit & HitCrit) != 0 ? "crit"
                       : (hit & HitGlancing) != 0 ? "glance"
                       : (hit & HitCrushing) != 0 ? "crush"
                       : "hit"
                };
            }

            var school = swing.SubDamages.Count > 0 ? swing.SubDamages[0].SchoolMask : 0;

            // A player's swing says nothing about creature damage; its only use is the armor below.
            if (attackerType == "creature")
            {
                var attackTime = offhand ? a.AttackTime1 : a.AttackTime0;
                var id = (swing.Attacker.Entry, a.Map, a.Visit, a.Owner, a.Level, attackTime, a.AuraKey, UnitType(swing.Victim),
                          kind, school, offhand, swing.MeleeSpellId);
                if (!_melee.TryGetValue(id, out var row))
                {
                    _melee[id] = row = new CreatureMeleeRecord
                    {
                        Entry = id.Entry,
                        Map = a.Map,
                        Visit = a.Visit,
                        Owner = a.Owner,
                        Level = a.Level,
                        AttackTime = attackTime,
                        Auras = a.AuraKey,
                        VictimType = id.Item8,
                        Kind = kind,
                        School = school,
                        Offhand = offhand,
                        MeleeSpell = swing.MeleeSpellId
                    };
                }

                row.Guids.Add(attacker);
                row.Swings++;
                if (kind is "hit" or "crit" or "glance" or "crush")
                {
                    var original = swing.OriginalDamage;
                    row.OriginalMin = Math.Min(row.OriginalMin, original);
                    row.OriginalMax = Math.Max(row.OriginalMax, original);
                    row.OriginalSum += original;
                    row.Originals.Add(original);
                }

                if (when != null)
                {
                    row.FirstUtc ??= when;
                    row.LastUtc = when;
                }
            }

            // Nothing but armor stands between the two numbers, so their ratio is the victim's
            // armor reduction. Overkill is left in: Damage still carries the full swing. Glancing
            // and crushing are left out, since whether OriginalDamage precedes them is unknown.
            const uint anyMitigation = HitFullAbsorb | HitPartialAbsorb | HitFullResist | HitPartialResist;
            if (kind is not ("hit" or "crit") || swing.Damage <= 0 || swing.BlockAmount != 0 ||
                (hit & anyMitigation) != 0 || swing.SubDamages.Any(s => s.Absorbed != 0 || s.Resisted != 0) ||
                school != 1)
                return;

            var armorId = (swing.Victim.Entry, v.Map, v.Visit, v.Level, v.Armor, v.AuraKey, attackerType, a.Level);
            if (!_armor.TryGetValue(armorId, out var armor))
            {
                _armor[armorId] = armor = new CreatureArmorRecord
                {
                    Entry = armorId.Entry,
                    Map = v.Map,
                    Visit = v.Visit,
                    Level = v.Level,
                    Armor = v.Armor,
                    Auras = v.AuraKey,
                    AttackerType = attackerType,
                    AttackerLevel = a.Level
                };
            }

            armor.Victims.Add(victim);
            armor.Attackers.Add(attacker);
            armor.Swings++;
            armor.OriginalSum += swing.OriginalDamage;
            armor.DamageSum += swing.Damage;
            if (swing.DebugArmorReduction != null)
            {
                armor.DebugSwings++;
                armor.DebugArmorReductionSum += swing.DebugArmorReduction.Value;
            }
        }

        /// <summary>
        /// Settles the difficulty of rows folded per stay, now that every stay's has been read,
        /// and folds together the stays that turned out to share one - a death and a run back
        /// is a new stay in the same instance.
        /// </summary>
        private static List<T> ByDifficulty<T>(IEnumerable<T> rows, Func<T, MapVisit> visit, Func<T, uint> map,
                                               Action<T, uint?> setDifficulty, Func<T, object> key, Action<T, T> into)
        {
            var merged = new Dictionary<object, T>();
            foreach (var row in rows)
            {
                setDifficulty(row, visit(row)?.DifficultyFor(map(row)));
                var k = key(row);
                if (merged.TryGetValue(k, out var held))
                    into(held, row);
                else
                    merged[k] = row;
            }

            return merged.Values.ToList();
        }

        private static DateTime? Earlier(DateTime? a, DateTime? b) => a == null ? b : b == null ? a : a < b ? a : b;
        private static DateTime? Later(DateTime? a, DateTime? b) => a == null ? b : b == null ? a : a > b ? a : b;

        private List<CreatureMeleeRecord> CollectCreatureMelee(ulong sniffId)
        {
            foreach (var row in _melee.Values)
                row.SniffId = sniffId;

            return ByDifficulty(_melee.Values, r => r.Visit, r => r.Map, (r, d) => r.Difficulty = d,
                r => (r.Entry, r.Map, r.Difficulty, r.Owner, r.Level, r.AttackTime, r.Auras, r.VictimType, r.Kind,
                      r.School, r.Offhand, r.MeleeSpell),
                (held, r) =>
                {
                    held.Guids.UnionWith(r.Guids);
                    held.Swings += r.Swings;
                    held.OriginalMin = Math.Min(held.OriginalMin, r.OriginalMin);
                    held.OriginalMax = Math.Max(held.OriginalMax, r.OriginalMax);
                    held.OriginalSum += r.OriginalSum;
                    held.Originals.AddRange(r.Originals);
                    held.FirstUtc = Earlier(held.FirstUtc, r.FirstUtc);
                    held.LastUtc = Later(held.LastUtc, r.LastUtc);
                });
        }

        private List<CreatureArmorRecord> CollectCreatureArmor(ulong sniffId)
        {
            foreach (var row in _armor.Values)
                row.SniffId = sniffId;

            return ByDifficulty(_armor.Values, r => r.Visit, r => r.Map, (r, d) => r.Difficulty = d,
                r => (r.Entry, r.Map, r.Difficulty, r.Level, r.Armor, r.Auras, r.AttackerType, r.AttackerLevel),
                (held, r) =>
                {
                    held.Victims.UnionWith(r.Victims);
                    held.Attackers.UnionWith(r.Attackers);
                    held.Swings += r.Swings;
                    held.OriginalSum += r.OriginalSum;
                    held.DamageSum += r.DamageSum;
                    held.DebugSwings += r.DebugSwings;
                    held.DebugArmorReductionSum += r.DebugArmorReductionSum;
                });
        }
    }
}
