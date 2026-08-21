using WowPacketParser.Enums;
using WowPacketParser.Misc;
using WowPacketParser.Store;
using WowPacketParser.Store.Objects;
using WowPacketParser.Parsing;

namespace WowPacketParserModule.V7_0_3_22248.Parsers
{
    public static class LootHandler
    {
        public static LootInstanceItemRecord ReadLootItem(Packet packet, params object[] indexes)
        {
            packet.ResetBitReader();

            packet.ReadBits("ItemType", 2, indexes);
            var uiType = packet.ReadBits("ItemUiType", 3, indexes);
            packet.ReadBit("CanTradeToTapList", indexes);

            var instance = Substructures.ItemHandler.ReadItemInstance(packet, indexes, "ItemInstance");

            var quantity = packet.ReadUInt32("Quantity", indexes);
            var lootItemType = packet.ReadByte("LootItemType", indexes);
            var slot = packet.ReadByte("LootListID", indexes);

            return new LootInstanceItemRecord
            {
                Slot = slot,
                ItemId = instance?.ItemID ?? 0,
                Quantity = quantity,
                UiType = (int)uiType,
                RandomPropertiesId = instance?.RandomPropertiesID ?? 0,
                LootItemType = lootItemType
            };
        }

        [Parser(Opcode.SMSG_LOOT_RESPONSE)]
        public static void HandleLootResponse(Packet packet)
        {
            var owner = packet.ReadPackedGuid128("Owner");
            var lootObj = packet.ReadPackedGuid128("LootObj");
            packet.ReadByteE<LootError>("FailureReason");
            var acquireReason = packet.ReadByteE<LootType>("AcquireReason");
            var lootMethod = packet.ReadByteE<LootMethod>("LootMethod");
            var threshold = packet.ReadByteE<ItemQuality>("Threshold");

            var coins = packet.ReadUInt32("Coins");

            var itemCount = packet.ReadUInt32("ItemCount");
            var currencyCount = packet.ReadUInt32("CurrencyCount");

            packet.ResetBitReader();

            packet.ReadBit("Acquired");
            packet.ReadBit("AELooting");
            if (ClientVersion.RemovedInVersion(ClientVersionBuild.V7_2_0_23826))
                packet.ReadBit("PersonalLooting");

            // An instance with no items and no coins is kept: it is the denominator for every
            // drop chance worked out from this data.
            var record = new LootInstanceRecord
            {
                OwnerGuid = owner == null ? null : $"0x{owner.High:X16}{owner.Low:X16}",
                LootObjectGuid = lootObj == null ? null : $"0x{lootObj.High:X16}{lootObj.Low:X16}",
                AcquireReason = (int)acquireReason,
                AcquireReasonName = acquireReason.ToString(),
                LootMethod = (int)lootMethod,
                LootMethodName = lootMethod.ToString(),
                Threshold = (int)threshold,
                Coins = coins,
                ItemCount = (int)itemCount,
                SeenUtc = packet.Time
            };

            for (var i = 0; i < itemCount; ++i)
                record.Items.Add(ReadLootItem(packet, i, "LootItem"));

            Storage.LootInstances.Add(record);

            for (var i = 0; i < currencyCount; ++i)
                V6_0_2_19033.Parsers.LootHandler.ReadCurrenciesData(packet, i, "Currencies");
        }

        [Parser(Opcode.SMSG_START_LOOT_ROLL)]
        public static void HandleLootStartRoll(Packet packet)
        {
            packet.ReadPackedGuid128("LootObj");
            packet.ReadInt32<MapId>("MapID");
            packet.ReadUInt32("RollTime");
            packet.ReadByte("ValidRolls");
            if (ClientVersion.AddedInVersion(ClientVersionBuild.V10_0_2_46479))
            {
                var lootRollIneligibilityReasonNum = 4;
                if (ClientVersion.AddedInVersion(ClientVersionBuild.V10_2_5_52902))
                    lootRollIneligibilityReasonNum = 5;

                for (var i = 0; i < lootRollIneligibilityReasonNum; i++)
                    packet.ReadUInt32E<LootRollIneligibilityReason>("LootRollIneligibleReason");
            }
            packet.ReadByteE<LootMethod>("Method");
            if (ClientVersion.AddedInVersion(ClientVersionBuild.V10_1_0_49407))
                packet.ReadInt32("DungeonEncounterID");
            ReadLootItem(packet, "LootItem");
        }

        [Parser(Opcode.SMSG_LOOT_ROLL)]
        public static void HandleLootRollServer(Packet packet)
        {
            packet.ReadPackedGuid128("LootObj");
            packet.ReadPackedGuid128("Winner");
            packet.ReadInt32("Roll");
            packet.ReadByte("RollType");
            ReadLootItem(packet, "LootItem");
            packet.ResetBitReader();
            packet.ReadBit("MainSpec");
        }

        [Parser(Opcode.SMSG_LOOT_ROLL_WON)]
        public static void HandleLootRollWon(Packet packet)
        {
            packet.ReadPackedGuid128("LootObj");
            packet.ReadPackedGuid128("Player");
            packet.ReadInt32("Roll");
            packet.ReadByte("RollType");
            if (ClientVersion.AddedInVersion(ClientVersionBuild.V10_1_0_49407))
                packet.ReadInt32("DungeonEncounterID");
            ReadLootItem(packet, "LootItem");
            packet.ReadBit("MainSpec");
        }

        [Parser(Opcode.SMSG_LOOT_ALL_PASSED)]
        public static void HandleLootAllPassed(Packet packet)
        {
            packet.ReadPackedGuid128("LootObj");
            if (ClientVersion.AddedInVersion(ClientVersionBuild.V10_1_0_49407))
                packet.ReadInt32("DungeonEncounterID");
            ReadLootItem(packet, "LootItem");
        }

        [Parser(Opcode.SMSG_LOOT_LIST, ClientVersionBuild.V7_2_0_23826)]
        public static void HandleLootList(Packet packet)
        {
            packet.ReadPackedGuid128("Owner");
            packet.ReadPackedGuid128("LootObj");

            var hasMaster = packet.ReadBit("HasMaster");
            var hasRoundRobin = packet.ReadBit("HasRoundRobinWinner");

            if (hasMaster)
                packet.ReadPackedGuid128("Master");

            if (hasRoundRobin)
                packet.ReadPackedGuid128("RoundRobinWinner");
        }
    }
}
