using WowPacketParser.Enums;
using WowPacketParser.Misc;
using WowPacketParser.Parsing;

namespace WowPacketParserModule.V2_5_1_38835.Parsers
{
    public static class CombatLogHandler
    {
        /// <summary>
        /// Without this, 2.5.1 to 2.5.4 fall through to 9.0.1's reader, whose build-number checks
        /// take a TBC build for a 9.1 one and read the 28-byte retail ContentTuning where the
        /// packet carries the 13-byte Classic one. Every swing ended in an exception.
        /// </summary>
        [Parser(Opcode.SMSG_ATTACKER_STATE_UPDATE)]
        public static void HandleAttackerStateUpdate(Packet packet)
        {
            var hasLogData = packet.ReadBit("HasLogData");

            if (hasLogData)
                WowPacketParserModule.V8_0_1_27101.Parsers.SpellHandler.ReadSpellCastLogData(packet);

            packet.ReadInt32("Size");

            V1_13_2_31446.Parsers.CombatLogHandler.ReadAttackRoundInfo(packet, packet.Holder.AttackerStateUpdate = new(),
                "AttackRoundInfo");
        }
    }
}
