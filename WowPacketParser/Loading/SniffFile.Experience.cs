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
        private readonly Dictionary<(uint, uint, MapVisit, uint, uint, uint, string, float), CreatureXpRecord> _xp = new();

        /// <summary>
        /// The sniffer's own character: the one the XP packets are addressed to. CMSG_PLAYER_LOGIN
        /// names it when the capture has one. Most start later, and 3.4.3 stores even the active
        /// player as a plain Player, so the fallback is the one player whose armor was sent -
        /// resistances are PRIVATE|OWNER, and no other player's ever arrive.
        /// </summary>
        private string _activePlayer;

        private void NotePlayerLogin(PacketPlayerLogin login) => _activePlayer = GuidKey(login.PlayerGuid) ?? _activePlayer;

        private void NoteOwnArmor(UniversalGuid guid, string key)
        {
            if (guid.Type == UniversalHighGuid.Player)
                _activePlayer ??= key;
        }

        private string ActivePlayer()
        {
            if (_activePlayer != null)
                return _activePlayer;

            foreach (var pair in Storage.Objects)
            {
                if (pair.Value.Item1.Type == ObjectType.ActivePlayer)
                    return _activePlayer = GuidKey(pair.Key);
            }

            return null;
        }

        /// <summary>
        /// A kill's XP, with the victim's level and the player's as they stood at the kill. The
        /// packet names neither level, and both move: the player levels up over a capture, and the
        /// level-up that a kill causes arrives after the kill's XP, so packet order gives the
        /// level the server computed with.
        /// </summary>
        private void FoldExperience(PacketLogXpGain gain)
        {
            // Reason 1 is quest, exploration and the like: no victim, and nothing about a creature.
            if (gain.Reason != 0 || gain.Victim == null)
                return;

            var victim = GuidKey(gain.Victim);
            var player = ActivePlayer();
            if (victim == null || player == null)
                return;

            var v = StateOf(victim, gain.Victim);
            var playerLevel = Tracked(player).Level;

            var id = (gain.Victim.Entry, v.Map, v.Visit, v.Zone, v.Level, playerLevel, v.Owner, gain.GroupBonus);
            if (!_xp.TryGetValue(id, out var row))
            {
                _xp[id] = row = new CreatureXpRecord
                {
                    Entry = id.Entry,
                    Map = v.Map,
                    Visit = v.Visit,
                    Zone = v.Zone,
                    Level = v.Level,
                    PlayerLevel = playerLevel,
                    Owner = v.Owner,
                    GroupBonus = gain.GroupBonus
                };
            }

            row.Guids.Add(victim);
            row.Kills++;
            row.AmountMin = Math.Min(row.AmountMin, gain.Amount);
            row.AmountMax = Math.Max(row.AmountMax, gain.Amount);
            row.AmountSum += gain.Amount;
            row.OriginalSum += gain.Original;
        }

        private List<CreatureXpRecord> CollectCreatureXp(ulong sniffId)
        {
            foreach (var row in _xp.Values)
                row.SniffId = sniffId;

            return ByDifficulty(_xp.Values, r => r.Visit, r => r.Map, (r, d) => r.Difficulty = d,
                r => (r.Entry, r.Map, r.Difficulty, r.Zone, r.Level, r.PlayerLevel, r.Owner, r.GroupBonus),
                (held, r) =>
                {
                    held.Guids.UnionWith(r.Guids);
                    held.Kills += r.Kills;
                    held.AmountMin = Math.Min(held.AmountMin, r.AmountMin);
                    held.AmountMax = Math.Max(held.AmountMax, r.AmountMax);
                    held.AmountSum += r.AmountSum;
                    held.OriginalSum += r.OriginalSum;
                });
        }
    }
}
