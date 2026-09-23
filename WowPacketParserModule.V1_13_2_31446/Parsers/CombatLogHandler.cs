using WowPacketParser.Enums;
using WowPacketParser.Misc;
using WowPacketParser.Parsing;
using WowPacketParser.Proto;

namespace WowPacketParserModule.V1_13_2_31446.Parsers
{
    public static class CombatLogHandler
    {
        public static void ReadCombatLogContentTuning(Packet packet, params object[] idx)
        {
            packet.ReadByte("Type", idx);
            packet.ReadByte("TargetLevel", idx);
            packet.ReadByte("Expansion", idx);
            packet.ReadByte("TargetMinScalingLevel", idx);
            packet.ReadByte("TargetMaxScalingLevel", idx);
            packet.ReadInt16("PlayerLevelDelta", idx);
            packet.ReadSByte("TargetScalingLevelDelta", idx);
            packet.ReadUInt16("PlayerItemLevel", idx);
            packet.ReadUInt16("ScalingHealthItemLevelCurveID", idx);
            packet.ReadByte("ScalesWithItemLevel", idx);
        }

        /// <param name="round">Filled when the caller is the swing itself; null for logs that repeat one.</param>
        public static void ReadAttackRoundInfo(Packet packet, PacketAttackerStateUpdate round, params object[] indexes)
        {
            var hitInfo = packet.ReadInt32E<SpellHitInfo>("HitInfo", indexes);

            var attacker = packet.ReadPackedGuid128("AttackerGUID", indexes);
            var victim = packet.ReadPackedGuid128("TargetGUID", indexes);

            var damage = packet.ReadInt32("Damage", indexes);
            var originalDamage = packet.ReadInt32("OriginalDamage", indexes);
            var overDamage = packet.ReadInt32("OverDamage", indexes);

            if (round != null)
            {
                round.HitInfo = (uint)hitInfo;
                round.Attacker = attacker.ToUniversalGuid();
                round.Victim = victim.ToUniversalGuid();
                round.Damage = damage;
                round.OriginalDamage = originalDamage;
                round.OverDamage = overDamage;
            }

            var subDmgCount = packet.ReadBool("HasSubDmg", indexes);
            if (subDmgCount)
            {
                var sub = new AttackSubDamage();
                sub.SchoolMask = (uint)packet.ReadInt32("SchoolMask", indexes);
                sub.FloatDamage = packet.ReadSingle("FloatDamage", indexes);
                sub.IntDamage = packet.ReadInt32("IntDamage", indexes);

                if (hitInfo.HasAnyFlag(SpellHitInfo.HITINFO_PARTIAL_ABSORB | SpellHitInfo.HITINFO_FULL_ABSORB))
                    sub.Absorbed = packet.ReadInt32("DamageAbsorbed", indexes);

                if (hitInfo.HasAnyFlag(SpellHitInfo.HITINFO_PARTIAL_RESIST | SpellHitInfo.HITINFO_FULL_RESIST))
                    sub.Resisted = packet.ReadInt32("DamageResisted", indexes);

                round?.SubDamages.Add(sub);
            }

            var victimState = packet.ReadByteE<VictimStates>("VictimState", indexes);
            packet.ReadInt32("AttackerState", indexes);

            var meleeSpell = packet.ReadInt32<SpellId>("MeleeSpellID", indexes);

            if (round != null)
            {
                round.VictimState = (uint)victimState;
                round.MeleeSpellId = meleeSpell;
            }

            if (hitInfo.HasAnyFlag(SpellHitInfo.HITINFO_BLOCK))
            {
                var block = packet.ReadInt32("BlockAmount", indexes);
                if (round != null)
                    round.BlockAmount = block;
            }

            if (hitInfo.HasAnyFlag(SpellHitInfo.HITINFO_RAGE_GAIN))
                packet.ReadInt32("RageGained", indexes);

            if (hitInfo.HasAnyFlag(SpellHitInfo.HITINFO_UNK0))
            {
                var armorReduction = packet.ReadInt32("ArmorReduction", indexes);
                if (round != null)
                    round.DebugArmorReduction = armorReduction;
                packet.ReadSingle("CritRollNeeded", indexes);
                packet.ReadSingle("CombatRoll", indexes);
                packet.ReadSingle("MissChance", indexes);
                packet.ReadSingle("DodgeChance", indexes);
                packet.ReadSingle("ParryChance", indexes);
                packet.ReadSingle("BlockChance", indexes);
                packet.ReadSingle("GlanceChance", indexes);
                packet.ReadSingle("CrushChance", indexes);
                packet.ReadSingle("MinDamage", indexes);
                packet.ReadSingle("MaxDamage", indexes);
                packet.ReadInt32("SinceLastSwing", indexes);
            }

            if (hitInfo.HasAnyFlag(SpellHitInfo.HITINFO_BLOCK | SpellHitInfo.HITINFO_UNK12))
                packet.ReadSingle("Unk Float", indexes);

            ReadCombatLogContentTuning(packet, indexes, "ContentTuning");
        }

        [Parser(Opcode.SMSG_ATTACKER_STATE_UPDATE)]
        public static void HandleAttackerStateUpdate(Packet packet)
        {
            var unkBit = packet.ReadBit("UnkBit");

            if (unkBit)
                packet.ReadSByte("UnkSByte");

            packet.ReadInt32("Size");

            ReadAttackRoundInfo(packet, packet.Holder.AttackerStateUpdate = new(), "AttackRoundInfo");
        }

        [Parser(Opcode.SMSG_SPELL_NON_MELEE_DAMAGE_LOG)]
        public static void HandleSpellNonMeleeDmgLog(Packet packet)
        {
            packet.ReadPackedGuid128("Me");
            packet.ReadPackedGuid128("CasterGUID");
            packet.ReadPackedGuid128("CastID");

            packet.ReadInt32<SpellId>("SpellID");
            packet.ReadInt32("SpellXSpellVisualID");
            packet.ReadInt32("Damage");
            packet.ReadInt32("OriginalDamage");
            packet.ReadInt32("OverKill");

            packet.ReadByte("SchoolMask");

            packet.ReadInt32("Absorbed");
            packet.ReadInt32("Resisted");
            packet.ReadInt32("ShieldBlock");

            packet.ResetBitReader();

            packet.ReadBit("Periodic");

            packet.ReadBitsE<AttackerStateFlags>("Flags", 7);

            var hasDebugData = packet.ReadBit("HasDebugData");
            var hasLogData = packet.ReadBit("HasLogData");
            var hasContentTuning = packet.ReadBit("HasContentTuning");

            if (hasContentTuning)
                V8_0_1_27101.Parsers.SpellHandler.ReadContentTuningParams(packet, "ContentTuning");

            if (hasDebugData)
                V8_0_1_27101.Parsers.CombatLogHandler.ReadSpellNonMeleeDebugData(packet, "DebugData");

            if (hasLogData)
                SpellHandler.ReadSpellCastLogData(packet, "SpellCastLogData");
        }
    }
}
