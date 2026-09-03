using Google.Protobuf;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using WowPacketParser.Enums;
using WowPacketParser.Enums.Version;
using WowPacketParser.Hotfix;
using WowPacketParser.Misc;
using WowPacketParser.PacketStructures;
using WowPacketParser.Parsing;
using WowPacketParser.Parsing.Parsers;
using WowPacketParser.Proto;
using WowPacketParser.Saving;
using WowPacketParser.SQL;
using WowPacketParser.Store;
using WowPacketParser.Store.Objects;

namespace WowPacketParser.Loading
{
    public class SniffFile
    {
        private string _fileName;
        private string _tempName;
        private FileCompression _compression;
        private SniffType _sniffType;

        private readonly Statistics _stats;
        private readonly DumpFormatType _dumpFormat;
        private readonly string _logPrefix;

        private SniffMetadata _sniffMetadata;
        private DateTime? _firstPacketTimeUtc;
        private DateTime? _lastPacketTimeUtc;
        private int _packetsRead;

        private readonly List<string> _withErrorHeaders = new List<string>();
        private readonly List<string> _skippedHeaders = new List<string>();
        private readonly List<string> _noStructureHeaders = new List<string>();

        public SniffFile(string fileName, DumpFormatType dumpFormat = DumpFormatType.Text, Tuple<int, int> number = null)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("fileName cannot be null, empty or whitespace.", nameof(fileName));

            _stats = new Statistics();

            FileName = fileName;
            _dumpFormat = dumpFormat;

            _logPrefix = number == null ? $"[{Path.GetFileName(FileName)}]" : $"[{number.Item1}/{number.Item2} {Path.GetFileName(FileName)}]";
        }

        private string FileName
        {
            get { return _fileName; }
            set
            {
                var extension = Path.GetExtension(value);
                if (extension == null)
                    throw new IOException($"Invalid file type {_fileName}");

                _compression = extension.ToFileCompressionEnum();

                _fileName = _compression != FileCompression.None ? value.Remove(value.Length - extension.Length) : value;

                extension = Path.GetExtension(_fileName);
                if (extension == null)
                    throw new IOException($"Invalid file type {_fileName}");

                switch (extension.ToLower())
                {
                    case ".bin":
                        _sniffType = SniffType.Bin;
                        break;
                    case ".pkt":
                        _sniffType = SniffType.Pkt;
                        break;
                    case ".sqlite":
                        _sniffType = SniffType.Sqlite;
                        break;
                    default:
                        throw new IOException($"Invalid file type {_fileName}");
                }
            }
        }

        public Packets ProcessFile()
        {

            try
            {
                return ProcessFileImpl();
            }
            catch (Exception ex)
            {
                Trace.WriteLine(_logPrefix + " " + ex.GetType());
                Trace.WriteLine(_logPrefix + " " + ex.Message);
                Trace.WriteLine(_logPrefix + " " + ex.StackTrace);
                return null;
            }
            finally
            {
                if (_tempName != null)
                {
                    File.Delete(_tempName);
                    Trace.WriteLine(_logPrefix + " Deleted temporary file " + Path.GetFileName(_tempName));
                }
            }
        }

        private Packets ProcessFileImpl()
        {
            if (_compression != FileCompression.None)
                _tempName = Decompress();

            switch (_dumpFormat)
            {
                case DumpFormatType.StatisticsPreParse:
                {
                    var packets = ReadPackets();
                    if (packets.Count == 0)
                        break;

                    var firstPacket = packets.First();
                    var lastPacket = packets.Last();

                    // CSV format
                    // ReSharper disable once UseStringInterpolation
                    Trace.WriteLine(string.Format("{0};{1};{2};{3};{4};{5};{6};{7};{8}",
                        FileName,                                                          // - sniff file name
                        firstPacket.Time,                                                  // - time of first packet
                        lastPacket.Time,                                                   // - time of last packet
                        (lastPacket.Time - firstPacket.Time).TotalSeconds,                 // - sniff duration (seconds)
                        packets.Count,                                                     // - packet count
                        packets.AsParallel().Sum(packet => packet.Length),                 // - total packets size (bytes)
                        packets.AsParallel().Average(packet => packet.Length),             // - average packet size (bytes)
                        packets.AsParallel().Min(packet => packet.Length),                 // - smaller packet size (bytes)
                        packets.AsParallel().Max(packet => packet.Length)));               // - larger packet size (bytes)

                    break;
                }
                case DumpFormatType.SniffDataOnly:
                case DumpFormatType.SqlOnly:
                case DumpFormatType.Text:
                case DumpFormatType.HexOnly:
                case DumpFormatType.UniversalProto:
                case DumpFormatType.UniversalProtoWithText:
                case DumpFormatType.UniversalProtoWithSeparateText:
                case DumpFormatType.Database:
                {
                    if (_dumpFormat == DumpFormatType.Database && !IsWithinContentCutoff())
                        break;

                    var outFileName = Path.ChangeExtension(FileName, null) + "_parsed.txt";
                    var outProtoFileName = Path.ChangeExtension(FileName, null) + "_parsed.dat";
                    FileStream protoOutputStream = null;

                    if (Settings.DumpFormatWithTextToFile())
                    {
                        if (Utilities.FileIsInUse(outFileName) && Settings.DumpFormat != DumpFormatType.SqlOnly)
                        {
                            // If our dump format requires a .txt to be created,
                            // check if we can write to that .txt before starting parsing
                            Trace.WriteLine($"Save file {outFileName} is in use, parsing will not be done.");
                            break;
                        }
                        File.Delete(outFileName);
                    }

                    if (_dumpFormat.IsUniversalProtobufType())
                    {
                        if (Utilities.FileIsInUse(outProtoFileName))
                        {
                            Trace.WriteLine($"Save file {outProtoFileName} is in use, parsing will not be done.");
                            break;
                        }
                        File.Delete(outProtoFileName);
                        protoOutputStream = File.Create(outProtoFileName);
                    }

                    Store.Store.SQLEnabledFlags = Settings.SQLOutputFlag;
                    bool movementEnabled = Settings.SQLOutputFlag.HasAnyFlagBit(SQLOutput.creature_movement) ||
                                           _dumpFormat == DumpFormatType.Database;

                    _stats.SetStartTime(DateTime.Now);

                    var threadCount = Settings.Threads;
                    if (threadCount == 0)
                        threadCount = Environment.ProcessorCount;

                    ThreadPool.SetMinThreads(threadCount + 2, 4);

                    var written = false;

                    Packets packets = new() { Version = StructureVersion.ProtobufStructureVersion, DumpType = (uint)Settings.DumpFormat };
                    using (var writer = (Settings.DumpFormatWithTextToFile() ? new StreamWriter(outFileName, true) : null))
                    {
                        var firstRead = true;
                        var firstWrite = true;

                        var reader = _compression != FileCompression.None ? new Reader(_tempName, _sniffType) : new Reader(FileName, _sniffType);

                        var pwp = new ParallelWorkProcessor<Packet>(() => // read
                        {
                            if (!reader.PacketReader.CanRead())
                                return Tuple.Create<Packet, bool>(null, true);

                            Packet packet;
                            var b = reader.TryRead(out packet);

                            if (firstRead)
                            {
                                Trace.WriteLine(
                                    $"{_logPrefix}: Parsing {Utilities.BytesToString(reader.PacketReader.GetTotalSize())} of packets. Detected version {ClientVersion.VersionString}");
                                packets.GameVersion = (ulong)ClientVersion.Build;
                                _sniffMetadata = reader.PacketReader.Metadata;
                                firstRead = false;
                            }

                            if (packet != null)
                            {
                                _firstPacketTimeUtc ??= packet.Time;
                                _lastPacketTimeUtc = packet.Time;
                                ++_packetsRead;

                                // In file order, on this thread: the gate's answer depends on
                                // map changes it has to have already seen, which the parallel
                                // stage below cannot promise.
                                if (packet.Direction != Direction.BNClientToServer &&
                                    packet.Direction != Direction.BNServerToClient)
                                {
                                    if (!IngestMapGate.Admit(packet, out var parsedHere))
                                        packet.Gated = true;
                                    else if (parsedHere)
                                        packet.ParsedInReader = true;
                                }
                            }

                            return Tuple.Create(packet, b);
                        }, packet => // parse
                        {
                            if (packet.Gated)
                            {
                                packet.Status = ParsedStatus.NotParsed;
                                _stats.AddByStatus(packet.Status);
                                return packet;
                            }

                            // Parse the packet, adding text to Writer and stuff to the stores
                            if (packet.Direction == Direction.BNClientToServer ||
                                packet.Direction == Direction.BNServerToClient)
                                BattlenetHandler.ParseBattlenet(packet);
                            else if (!packet.ParsedInReader)
                                Handler.Parse(packet);

                            // Update statistics
                            _stats.AddByStatus(packet.Status);
                            return packet;
                        },
                        packet => // write
                        {
                            if (!Console.IsOutputRedirected)
                                ShowPercentProgress("Processing...", reader.PacketReader.GetCurrentSize(), reader.PacketReader.GetTotalSize());
                            else
                                Console.WriteLine(reader.PacketReader.GetCurrentSize() * 1.0 / reader.PacketReader.GetTotalSize());

                            if (!packet.Status.HasAnyFlag(Settings.OutputFlag) || !packet.WriteToFile)
                            {
                                packet.ClosePacket();
                                return;
                            }

                            written = true;

                            if (firstWrite)
                            {
                                // ReSharper disable AccessToDisposedClosure
                                writer?.WriteLine(GetHeader(FileName));
                                // ReSharper restore AccessToDisposedClosure

                                firstWrite = false;
                            }

                            // get packet header if necessary
                            if (Settings.LogPacketErrors)
                            {
                                switch (packet.Status)
                                {
                                    case ParsedStatus.WithErrors:
                                        _withErrorHeaders.Add(packet.GetHeader());
                                        break;
                                    case ParsedStatus.NotParsed:
                                        _skippedHeaders.Add(packet.GetHeader());
                                        break;
                                    case ParsedStatus.NoStructure:
                                        _noStructureHeaders.Add(packet.GetHeader());
                                        break;
                                }
                            }

// ReSharper disable AccessToDisposedClosure
                            if (writer != null)
                            {
                                // Write to file
                                var startOffset = writer.BaseStream.Position;
                                writer.WriteLine(packet.Writer);
                                writer.Flush();

                                if (_dumpFormat is DumpFormatType.UniversalProtoWithSeparateText)
                                {
                                    packet.Holder.BaseData.TextStartOffset = startOffset;
                                    packet.Holder.BaseData.TextLength = (int)(writer.BaseStream.Position - startOffset);
                                }
                            }
// ReSharper restore AccessToDisposedClosure

                            if (_dumpFormat is DumpFormatType.UniversalProtoWithText)
                                packet.Holder.BaseData.StringData = packet.Writer.ToString();

                            // Close Writer, Stream - Dispose
                            packet.ClosePacket();

                            if (_dumpFormat.IsUniversalProtobufType() || movementEnabled || HotfixSettings.Instance.ShouldLog())
                            {
                                if (_dumpFormat.IsUniversalProtobufType() || HotfixSettings.Instance.ShouldLog())
                                    packets.Packets_.Add(packet.Holder);
                                else if (movementEnabled && WantedByIngest(packet.Holder))
                                    packets.Packets_.Add(packet.Holder);
                            }
                        }, threadCount);

                        pwp.WaitForFinished(Timeout.Infinite);

                        reader.PacketReader.Dispose();

                        if (protoOutputStream != null)
                        {
                            packets.WriteTo(protoOutputStream);
                            protoOutputStream.Close();
                        }

                        _stats.SetEndTime(DateTime.Now);
                    }

                    if (Settings.DumpFormatWithTextToFile())
                    {
                        if (written)
                            Trace.WriteLine($"{_logPrefix}: Saved file to '{outFileName}'");
                        else
                        {
                            Trace.WriteLine($"{_logPrefix}: No file produced");
                            File.Delete(outFileName);
                        }
                    }

                    Trace.WriteLine($"{_logPrefix}: {_stats}");

                    if (IngestMapGate.Enabled && IngestMapGate.GatedCount > 0)
                    {
                        var dropped = IngestMapGate.Census
                            .Where(c => c.Key != uint.MaxValue && !IngestMapGate.IsMapWanted(c.Key))
                            .OrderByDescending(c => c.Value)
                            .Select(c => $"{c.Key} ({c.Value})");

                        Trace.WriteLine($"{_logPrefix}: map gate dropped {IngestMapGate.GatedCount} of " +
                                        $"{_packetsRead} packets ({IngestMapGate.GatedCount * 100.0 / _packetsRead:F1}%) " +
                                        $"on maps {string.Join(", ", dropped)}");
                    }

                    if (_dumpFormat == DumpFormatType.Database)
                        SaveToIngestDatabase(packets);

                    if (Settings.SQLOutputFlag != 0 || HotfixSettings.Instance.ShouldLog())
                        WriteSQLs(packets);

                    if (Settings.LogPacketErrors)
                        WritePacketErrors();

                    GC.Collect(); // Force a GC collect after parsing a file. It seems to help.

                    return packets;
                }
                case DumpFormatType.Pkt:
                {
                    var packets = ReadPackets();
                    if (packets.Count == 0)
                        break;

                    if (Settings.FilterPacketsNum < 0)
                    {
                        var packetsPerSplit = Math.Abs(Settings.FilterPacketsNum);
                        var totalPackets = packets.Count;

                        var numberOfSplits = (int)Math.Ceiling((double)totalPackets / packetsPerSplit);

                        for (var i = 0; i < numberOfSplits; ++i)
                        {
                            var fileNamePart = FileName + "_part_" + (i + 1) + ".pkt";

                            var packetsPart = packets.Take(packetsPerSplit).ToList();
                            packets.RemoveRange(0, packetsPart.Count);

                            BinaryDump(fileNamePart, packetsPart);
                        }
                    }
                    else
                    {
                        var fileNameExcerpt = Path.ChangeExtension(FileName, null) + "_excerpt.pkt";
                        BinaryDump(fileNameExcerpt, packets);
                    }

                    break;
                }
                case DumpFormatType.PktSplit:
                {
                    var packets = ReadPackets();
                    if (packets.Count == 0)
                        break;

                    SplitBinaryDump(packets);
                    break;
                }
                case DumpFormatType.PktDirectionSplit:
                {
                    var packets = ReadPackets();
                    if (packets.Count == 0)
                        break;

                    DirectionSplitBinaryDump(packets);
                    break;
                }
                case DumpFormatType.PktSessionSplit:
                {
                    var packets = ReadPackets();
                    if (packets.Count == 0)
                        break;

                    SessionSplitBinaryDump(packets);
                    break;
                }
                case DumpFormatType.CompressSniff:
                {
                    if (_compression != FileCompression.None)
                    {
                        Trace.WriteLine($"Skipped compressing file {FileName}");
                        break;
                    }

                    Compress();
                    break;
                }
                case DumpFormatType.SniffVersionSplit:
                {
                    var reader = _compression != FileCompression.None ? new Reader(_tempName, _sniffType) : new Reader(FileName, _sniffType);

                    if (ClientVersion.IsUndefined() && reader.PacketReader.CanRead())
                    {
                        Packet packet;
                        reader.TryRead(out packet);
                        packet.ClosePacket();
                    }

                    reader.PacketReader.Dispose();

                    var version = ClientVersion.IsUndefined() ? "unknown" : ClientVersion.VersionString;

                    var realFileName = GetCompressedFileName();

                    var destPath = Path.Combine(Path.GetDirectoryName(realFileName), version,
                        Path.GetFileName(realFileName));

                    var destDir = Path.GetDirectoryName(destPath);
                    if (!Directory.Exists(destDir))
                        Directory.CreateDirectory(destDir);

                    File.Move(realFileName, destPath);

                    Trace.WriteLine("Moved " + realFileName + " to " + destPath);

                    break;
                }
                case DumpFormatType.ConnectionIndexes:
                {
                    var packets = ReadPackets();
                    if (packets.Count == 0)
                        break;

                    using (var writer = new StreamWriter(Path.ChangeExtension(FileName, null) + "_connidx.txt"))
                    {
                        if (ClientVersion.Build <= ClientVersionBuild.V6_0_3_19342)
                            writer.WriteLine("# Warning: versions before 6.1 might not have proper ConnectionIndex values.");

                        IEnumerable<IGrouping<Tuple<int, Direction>, Packet>> groupsOpcode = packets
                            .GroupBy(packet => Tuple.Create(packet.Opcode, packet.Direction))
                            .OrderBy(grouping => grouping.Key.Item2);

                        foreach (var groupOpcode in groupsOpcode)
                        {
                            var groups = groupOpcode
                                .GroupBy(packet => packet.ConnectionIndex)
                                .OrderBy(grouping => grouping.Key)
                                .ToList();

                            writer.Write("{0} {1,-50}: ", groupOpcode.Key.Item2, Opcodes.GetOpcodeName(groupOpcode.Key.Item1, groupOpcode.Key.Item2));

                            for (var i = 0; i < groups.Count; i++)
                            {
                                var idx = groups[i].Key;
                                writer.Write("{0} ({1}{2})", idx, (idx & 1) != 0 ? "INSTANCE" : "REALM", (idx & 2) != 0 ? "_NEW" : "");

                                if (i != groups.Count - 1)
                                    writer.Write(", ");
                            }

                            writer.WriteLine();
                        }
                    }

                    break;
                }
                case DumpFormatType.Fusion:
                {
                    var packets = ReadPackets();
                    if (packets.Count == 0)
                        break;

                    FusionDump(packets);
                    break;
                }
                default:
                {
                    Trace.WriteLine($"{_logPrefix}: Dump format is none, nothing will be processed.");
                    break;
                }
            }

            return null;
        }

        public static string GetHeader(string fileName)
        {
            return "# TrinityCore - WowPacketParser" + Environment.NewLine +
                   "# File name: " + Path.GetFileName(fileName) + Environment.NewLine +
                   "# Detected build: " + ClientVersion.Build + Environment.NewLine +
                   "# Detected locale: " + ClientLocale.ClientLocaleString + Environment.NewLine +
                   "# Targeted database: " + Settings.TargetedDatabase + Environment.NewLine +
                   "# Parsing date: " + DateTime.Now.ToString(CultureInfo.InvariantCulture) + Environment.NewLine;
        }

        private static long _lastPercent;
        static void ShowPercentProgress(string message, long curr, long total)
        {
            var percent = (100 * curr) / total;
            if (percent == _lastPercent)
                return; // we only need to update if percentage changes otherwise we would be wasting precious resources

            _lastPercent = percent;

            Console.Write("\r{0} {1}% complete", message, percent);
            if (curr == total)
                Console.WriteLine();
        }

        public List<Packet> ReadPackets()
        {
            var packets = new List<Packet>();

            // stats.SetStartTime(DateTime.Now);

            var fileName = FileName;
            if (_compression != FileCompression.None)
                fileName = _tempName;

            Reader.Read(fileName, _sniffType, p =>
            {
                var packet = p.Item1;
                var currSize = p.Item2;
                var totalSize = p.Item3;

                ShowPercentProgress("Reading...", currSize, totalSize);
                packets.Add(packet);
            });

            return packets;

            // stats.SetEndTime(DateTime.Now);
            // Trace.WriteLine(string.Format("{0}: {1}", _logPrefix, _stats));
        }

        private void FusionDump(ICollection<Packet> packets)
        {
            Trace.WriteLine($"{_logPrefix}: Merge {packets.Count} packets to a file...");
            FusionBinaryPacketWriter.Write(packets);
        }

        private void SplitBinaryDump(ICollection<Packet> packets)
        {
            Trace.WriteLine($"{_logPrefix}: Splitting {packets.Count} packets to multiple files...");
            SplitBinaryPacketWriter.Write(packets);
        }

        private void DirectionSplitBinaryDump(ICollection<Packet> packets)
        {
            Trace.WriteLine($"{_logPrefix}: Splitting {packets.Count} packets to multiple files...");
            SplitDirectionBinaryPacketWriter.Write(packets);
        }

        private void SessionSplitBinaryDump(ICollection<Packet> packets)
        {
            Trace.WriteLine($"{_logPrefix}: Splitting {packets.Count} packets to multiple files...");
            SplitSessionBinaryPacketWriter.Write(packets);
        }

        private void BinaryDump(string fileName, ICollection<Packet> packets)
        {
            Trace.WriteLine($"{_logPrefix}: Copying {packets.Count} packets to .pkt format...");
            BinaryPacketWriter.Write(fileName, FileMode.Create, packets);
        }

        /// <summary>
        /// Reads just the header to find out which expansion's world this sniff shows, and says
        /// whether it is worth parsing at all. Cheap enough to do before the real pass, so an
        /// overnight run does not spend hours on sniffs that will be filtered out later anyway.
        /// </summary>
        private bool IsWithinContentCutoff()
        {
            var cutoff = Settings.IngestMaxContentExpansion;
            if (cutoff == ClientType.Current)
                return true;

            var peek = new Reader(_compression != FileCompression.None ? _tempName : FileName, _sniffType);
            peek.PacketReader.Dispose();

            if (ClientVersion.IsUndefined())
                return true;

            var content = ClientVersion.ContentExpansion;
            if (content <= cutoff)
                return true;

            Trace.WriteLine($"{_logPrefix}: Skipped - {ClientVersion.VersionString} is {content} content " +
                            $"({ClientVersion.Branch} branch), past the {cutoff} cutoff");
            return false;
        }

        /// <summary>
        /// Pours everything this sniff produced into the ingest database, then empties the
        /// stores. Nothing else clears them when no SQL output is configured, so without this
        /// a batch run would carry one sniff's objects into the next one's rows.
        /// </summary>
        private void SaveToIngestDatabase(Packets packets)
        {
            var sniffId = SaveSniffRow();
            if (sniffId != 0)
            {
                var coverage = new List<SniffCoverageRecord>();
                var maps = new Dictionary<uint, SniffMapRecord>();

                SniffMapRecord MapRow(uint map)
                {
                    if (!maps.TryGetValue(map, out var row))
                        maps[map] = row = new SniffMapRecord { Map = map };
                    return row;
                }

                var gameObjects = CollectGameObjectSpawns(sniffId);
                var goWritten = IngestDatabase.SaveGameObjectSpawns(sniffId, gameObjects);
                if (gameObjects.Count > 0)
                    Trace.WriteLine($"{_logPrefix}: {goWritten} gameobject spawns recorded");
                foreach (var go in gameObjects)
                    MapRow(go.Map).GameObjectSpawns++;
                coverage.Add(Coverage(CollectorVersion.GameObjectSpawn, CollectorVersion.GameObjectSpawnVersion,
                                      goWritten, Opcode.SMSG_UPDATE_OBJECT));

                var creatures = CollectCreatureSpawns(sniffId);
                var creatureWritten = IngestDatabase.SaveCreatureSpawns(sniffId, creatures);
                if (creatures.Count > 0)
                    Trace.WriteLine($"{_logPrefix}: {creatureWritten} creature spawns recorded");
                foreach (var c in creatures)
                    MapRow(c.Map).CreatureSpawns++;
                coverage.Add(Coverage(CollectorVersion.CreatureSpawn, CollectorVersion.CreatureSpawnVersion,
                                      creatureWritten, Opcode.SMSG_UPDATE_OBJECT));

                var waypoints = CollectCreatureWaypoints(sniffId, packets);
                var wpWritten = IngestDatabase.SaveCreatureWaypoints(sniffId, waypoints);
                if (waypoints.Count > 0)
                    Trace.WriteLine($"{_logPrefix}: {wpWritten} waypoints recorded");
                foreach (var w in waypoints)
                    MapRow(w.Map).Waypoints++;
                coverage.Add(Coverage(CollectorVersion.CreatureWaypoint, CollectorVersion.CreatureWaypointVersion,
                                      wpWritten, Opcode.SMSG_ON_MONSTER_MOVE));

                var movement = SummariseMovement(sniffId, waypoints);
                var moveWritten = IngestDatabase.SaveCreatureMovement(sniffId, movement);
                coverage.Add(Coverage(CollectorVersion.CreatureMovement, CollectorVersion.CreatureMovementVersion,
                                      moveWritten, Opcode.SMSG_ON_MONSTER_MOVE));

                // Mists introduced area looting, which changed what a loot response means: one
                // response can cover several corpses at once. The collector's whole model is one
                // loot per owner, so on those builds it would record confident nonsense rather
                // than nothing. Refusing is the honest outcome, and the coverage row says why
                // instead of leaving a silent zero that looks like a player who never looted.
                var lootSupported = ClientVersion.ContentExpansion < ClientType.MistsOfPandaria;
                var loots = lootSupported ? CollectLootInstances(sniffId) : new List<LootInstanceRecord>();
                var lootWritten = lootSupported ? IngestDatabase.SaveLootInstances(sniffId, loots) : 0;
                if (loots.Count > 0)
                    Trace.WriteLine($"{_logPrefix}: {lootWritten} loot instances recorded");
                foreach (var l in loots)
                {
                    if (l.Map != null)
                        MapRow(l.Map.Value).LootInstances++;
                }

                coverage.Add(lootSupported
                    ? Coverage(CollectorVersion.Loot, CollectorVersion.LootVersion,
                               lootWritten, Opcode.SMSG_LOOT_RESPONSE)
                    : new SniffCoverageRecord
                    {
                        Capability = CollectorVersion.Loot,
                        Status = CollectorVersion.StatusUnsupported,
                        CollectorVersion = CollectorVersion.LootVersion,
                        RowsWritten = 0,
                        Reason = $"{ClientVersion.ContentExpansion} has area looting; one response " +
                                 "can cover several corpses and the one-loot-per-owner model does not hold"
                    });


                var spellCasts = CollectCreatureSpellCasts(sniffId, packets, out var spellTargets, out var spellDests);
                var spellWritten = IngestDatabase.SaveCreatureSpellCasts(sniffId, spellCasts);
                if (spellCasts.Count > 0)
                    Trace.WriteLine($"{_logPrefix}: {spellWritten} creature spell casts recorded");
                foreach (var cast in spellCasts)
                    MapRow(cast.Map).CreatureSpells++;
                coverage.Add(Coverage(CollectorVersion.CreatureSpell, CollectorVersion.CreatureSpellVersion,
                                      spellWritten, Opcode.SMSG_SPELL_START));

                var targetWritten = IngestDatabase.SaveSpellTargets(sniffId, spellTargets);
                coverage.Add(Coverage(CollectorVersion.SpellTarget, CollectorVersion.SpellTargetVersion,
                                      targetWritten, Opcode.SMSG_SPELL_GO));

                var destWritten = IngestDatabase.SaveSpellDestinations(sniffId, spellDests);
                coverage.Add(Coverage(CollectorVersion.SpellDestination, CollectorVersion.SpellDestinationVersion,
                                      destWritten, Opcode.SMSG_SPELL_GO));

                var equipment = CollectCreatureEquipment(sniffId);
                var equipWritten = IngestDatabase.SaveCreatureEquipment(sniffId, equipment);
                if (equipment.Count > 0)
                    Trace.WriteLine($"{_logPrefix}: {equipWritten} creature equipment sets recorded");
                coverage.Add(Coverage(CollectorVersion.CreatureEquip, CollectorVersion.CreatureEquipVersion,
                                      equipWritten, Opcode.SMSG_UPDATE_OBJECT));

                var creatureAuras = CollectCreatureAuras(sniffId);
                var auraWritten = IngestDatabase.SaveCreatureAuras(sniffId, creatureAuras);
                if (creatureAuras.Count > 0)
                    Trace.WriteLine($"{_logPrefix}: {auraWritten} creature auras recorded");
                coverage.Add(Coverage(CollectorVersion.CreatureAura, CollectorVersion.CreatureAuraVersion,
                                      auraWritten, Opcode.SMSG_AURA_UPDATE));

                var gossipMenus = CollectGossip(sniffId, packets, out var gossipOptions, out var npcTexts);
                var gossipWritten = IngestDatabase.SaveGossipMenus(sniffId, gossipMenus);
                IngestDatabase.SaveGossipMenuOptions(sniffId, gossipOptions);
                IngestDatabase.SaveNpcTexts(sniffId, npcTexts);
                if (gossipMenus.Count > 0)
                    Trace.WriteLine($"{_logPrefix}: {gossipWritten} gossip menus, {gossipOptions.Count} options, " +
                                    $"{npcTexts.Count} texts recorded");
                coverage.Add(Coverage(CollectorVersion.Gossip, CollectorVersion.GossipVersion,
                                      gossipWritten, Opcode.SMSG_GOSSIP_MESSAGE));

                var teleports = CollectAreaTriggerTeleports(sniffId, packets);
                var teleWritten = IngestDatabase.SaveAreaTriggerTeleports(sniffId, teleports);
                if (teleports.Count > 0)
                    Trace.WriteLine($"{_logPrefix}: {teleWritten} areatrigger teleports recorded");
                coverage.Add(Coverage(CollectorVersion.AreaTriggerTeleport, CollectorVersion.AreaTriggerTeleportVersion,
                                      teleWritten, Opcode.CMSG_AREA_TRIGGER));

                // The census counts every packet, including maps that yielded nothing and maps
                // that were gated away entirely - which is the only record that they were ever
                // in the file, since nothing else survives the gate.
                foreach (var seen in IngestMapGate.Census)
                {
                    if (seen.Key == uint.MaxValue)
                        continue;

                    var row = MapRow(seen.Key);
                    row.Packets = seen.Value;
                    if (!IngestMapGate.IsMapWanted(seen.Key))
                        row.GatedPackets = seen.Value;
                }

                IngestDatabase.SaveSniffMaps(sniffId, maps.Values.ToList());
                IngestDatabase.SaveCoverage(sniffId, coverage);

                var gaps = coverage.Where(c => c.Status == CollectorVersion.StatusUnsupported).ToList();
                if (gaps.Count > 0)
                    Trace.WriteLine($"{_logPrefix}: no {string.Join(", ", gaps.Select(g => g.Capability))} " +
                                    $"- {gaps[0].Reason}");
            }

            Storage.ClearContainers();
        }

        /// <summary>
        /// Reduces a sniff's waypoints to one row per creature. Done here, while the points are
        /// already in memory, because doing it afterwards means aggregating tens of millions of
        /// rows - a single grouped pass over the waypoint table runs for over a quarter of an
        /// hour, which is not a thing anyone will do twice.
        ///
        /// Grouping is by guid, not by entry: the same creature entry can patrol at one spawn
        /// point and wander at another, so movement kind is a property of the spawn.
        /// </summary>
        private static List<CreatureMovementRecord> SummariseMovement(
            ulong sniffId, List<CreatureWaypointRecord> waypoints)
        {
            var byCreature = new Dictionary<string, List<CreatureWaypointRecord>>();
            foreach (var w in waypoints)
            {
                if (!byCreature.TryGetValue(w.Guid, out var list))
                    byCreature[w.Guid] = list = new List<CreatureWaypointRecord>();
                list.Add(w);
            }

            var result = new List<CreatureMovementRecord>(byCreature.Count);
            foreach (var pair in byCreature)
            {
                var points = pair.Value;

                var xs = new List<float>(points.Count);
                var ys = new List<float>(points.Count);
                var zs = new List<float>(points.Count);
                var segments = new HashSet<int>();
                var multi = new HashSet<int>();

                foreach (var w in points)
                {
                    xs.Add(w.PositionX);
                    ys.Add(w.PositionY);
                    zs.Add(w.PositionZ);
                    segments.Add(w.SegmentId);
                    if (w.SegmentPoints > 1)
                        multi.Add(w.SegmentId);
                }

                var mx = Median(xs);
                var my = Median(ys);
                var mz = Median(zs);

                var distances = new List<float>(points.Count);
                foreach (var w in points)
                {
                    var dx = w.PositionX - mx;
                    var dy = w.PositionY - my;
                    distances.Add((float)Math.Sqrt(dx * dx + dy * dy));
                }
                distances.Sort();

                // A creature is counted as having paused when the next order arrived after the
                // one it was given had already run its stated course. Random movement waits
                // between destinations; an authored path only stops for roleplay or at its ends.
                var ordered = new List<CreatureWaypointRecord>();
                foreach (var w in points)
                {
                    if (w.PointIndex == 0)
                        ordered.Add(w);
                }
                ordered.Sort((a, b) => Comparer<DateTime?>.Default.Compare(a.SeenUtc, b.SeenUtc));

                var transitions = 0;
                var pauses = 0;
                for (var i = 0; i + 1 < ordered.Count; i++)
                {
                    var from = ordered[i];
                    var to = ordered[i + 1];
                    if (!from.SeenUtc.HasValue || !to.SeenUtc.HasValue || !from.MoveTimeMs.HasValue)
                        continue;

                    transitions++;
                    var idle = (to.SeenUtc.Value - from.SeenUtc.Value).TotalMilliseconds - from.MoveTimeMs.Value;
                    if (idle > 1500)
                        pauses++;
                }

                DateTime? first = null, last = null;
                foreach (var w in points)
                {
                    if (!w.SeenUtc.HasValue)
                        continue;
                    if (first == null || w.SeenUtc < first)
                        first = w.SeenUtc;
                    if (last == null || w.SeenUtc > last)
                        last = w.SeenUtc;
                }

                result.Add(new CreatureMovementRecord
                {
                    SniffId = sniffId,
                    Guid = pair.Key,
                    Entry = points[0].Entry,
                    Map = points[0].Map,
                    Points = points.Count,
                    Segments = segments.Count,
                    MultiPointSegments = multi.Count,
                    Transitions = transitions,
                    Pauses = pauses,
                    MedianX = mx,
                    MedianY = my,
                    MedianZ = mz,
                    Radius = distances[distances.Count - 1],
                    RadiusRobust = distances[(int)(0.99 * (distances.Count - 1))],
                    FirstSeenUtc = first,
                    LastSeenUtc = last
                });
            }

            return result;
        }

        private static float Median(List<float> values)
        {
            values.Sort();
            var mid = values.Count / 2;
            return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2f;
        }

        /// <summary>
        /// One coverage row for one kind of data. Zero rows only counts as a gap worth revisiting
        /// when this build's opcode table has no entry for the message the data comes from: the
        /// Classic rebuild branches ship stub tables that map movement but not loot, and those
        /// sniffs must not be mistaken for ones where the player simply never looted anything.
        /// </summary>
        private static SniffCoverageRecord Coverage(string capability, int version, int written, Opcode needs)
        {
            // Rows are proof the opcode was there, whatever the table says. Without this a
            // client-to-server opcode reads as unsupported on every build, because the lookup
            // below only ever asks the server-to-client table.
            if (written == 0 &&
                Opcodes.GetOpcode(needs, Direction.ServerToClient) == 0 &&
                Opcodes.GetOpcode(needs, Direction.ClientToServer) == 0)
            {
                return new SniffCoverageRecord
                {
                    Capability = capability,
                    Status = CollectorVersion.StatusUnsupported,
                    CollectorVersion = version,
                    RowsWritten = 0,
                    Reason = $"build {ClientVersion.BuildInt} has no {needs} in its opcode table"
                };
            }

            return new SniffCoverageRecord
            {
                Capability = capability,
                Status = written > 0 ? CollectorVersion.StatusOk : CollectorVersion.StatusEmpty,
                CollectorVersion = version,
                RowsWritten = written
            };
        }

        private List<GameObjectSpawnRecord> CollectGameObjectSpawns(ulong sniffId)
        {
            var spawns = new List<GameObjectSpawnRecord>();

            foreach (var pair in Storage.Objects)
            {
                var obj = pair.Value.Item1;
                if (obj.Type != ObjectType.GameObject || obj.IsTemporarySpawn())
                    continue;

                var entry = obj.ObjectData.EntryID;
                if (entry == null || entry == 0)
                    continue;

                // Objects that never carried a position - bobbers, loot containers and the like.
                // Nothing to say about a spawn point, and they would otherwise all pile up on 0,0,0.
                var pos = obj.Movement.Position;
                if (pos.X == 0 && pos.Y == 0 && pos.Z == 0)
                    continue;

                // The pre-3.0 branch of GetStaticRotation reads a raw update field array that is
                // not present on every object. A missing rotation is worth a null column, not a
                // lost spawn.
                Quaternion? rotation = null;
                try
                {
                    if (obj is GameObject go)
                        rotation = go.GetStaticRotation();
                }
                catch (Exception)
                {
                    rotation = null;
                }

                spawns.Add(new GameObjectSpawnRecord
                {
                    SniffId = sniffId,
                    Guid = $"0x{pair.Key.High:X16}{pair.Key.Low:X16}",
                    Entry = (uint)entry,
                    Map = obj.Map,
                    AreaId = obj.Area != -1 ? obj.Area : null,
                    ZoneId = obj.Zone != -1 ? obj.Zone : null,
                    PositionX = obj.Movement.Position.X,
                    PositionY = obj.Movement.Position.Y,
                    PositionZ = obj.Movement.Position.Z,
                    Orientation = obj.Movement.Orientation,
                    Rotation0 = rotation?.X,
                    Rotation1 = rotation?.Y,
                    Rotation2 = rotation?.Z,
                    Rotation3 = rotation?.W,
                    CreateType = (int)obj.CreateType,
                    PhaseMask = obj.PhaseMask != 0 ? obj.PhaseMask : null,
                    Phases = obj.Phases != null && obj.Phases.Count > 0 ? string.Join(" - ", obj.Phases) : null,
                    FirstSeenUtc = _firstPacketTimeUtc.HasValue && pair.Value.Item2.HasValue
                        ? _firstPacketTimeUtc.Value + pair.Value.Item2.Value
                        : _firstPacketTimeUtc
                });
            }

            return spawns;
        }

        /// <summary>
        /// Every creature in this sniff that could plausibly be a world spawn.
        ///
        /// Owned creatures - pets, guardians, totems, anything with CreatedBySpell - are dropped
        /// by IsTemporarySpawn. Corpses are dropped outright rather than flagged: a dead
        /// creature's position is where it died, which says nothing about where it spawns.
        /// </summary>

        /// <summary>
        /// Every spell a creature began casting, plus what its completed casts hit and where
        /// they were aimed.
        ///
        /// Starts are kept as raw rows: the gap between two of them is the timer observation,
        /// and since those timers have no upper bound, deciding here which quantile stands in
        /// for a maximum would be deciding it before anyone has looked at the data.
        ///
        /// Players are excluded. So are pets, which on this high guid are usually a player's.
        /// </summary>
        private List<CreatureSpellCastRecord> CollectCreatureSpellCasts(ulong sniffId, Packets packets,
                                                                        out List<SpellTargetRecord> targets,
                                                                        out List<SpellDestinationRecord> destinations)
        {
            var casts = new List<CreatureSpellCastRecord>();
            targets = new List<SpellTargetRecord>();
            destinations = new List<SpellDestinationRecord>();

            if (packets == null)
                return casts;

            // Spell packets carry no map, so take it from the object that cast.
            var units = new Dictionary<string, (uint Entry, uint Map)>();
            foreach (var pair in Storage.Objects)
            {
                var obj = pair.Value.Item1;
                if (obj.Type != ObjectType.Unit)
                    continue;

                var unitEntry = obj.ObjectData?.EntryID;
                if (unitEntry == null || unitEntry == 0)
                    continue;

                units[GuidKey(pair.Key)] = ((uint)unitEntry, obj.Map);
            }

            var targetHits = new Dictionary<(uint Spell, uint Caster, uint Target), SpellTargetRecord>();
            var dests = new Dictionary<(uint Spell, uint Caster, float X, float Y, float Z), SpellDestinationRecord>();

            // A start and the go that completes it share a cast id, and one cast can be
            // reported more than once when it hits several targets. Both are resolved by id:
            // the first start of an id becomes the row, and any go with that id marks it done.
            var startByCast = new Dictionary<string, CreatureSpellCastRecord>();
            var seenStarts = new HashSet<string>();

            foreach (var holder in packets.Packets_)
            {
                var go = holder.SpellGo?.Data;
                var start = holder.SpellStart?.Data;
                var data = go ?? start;
                if (data?.Caster == null || data.Spell == 0)
                    continue;

                var casterKey = GuidKey(data.Caster);
                var casterIsCreature = data.Caster.Type == UniversalHighGuid.Creature ||
                                       data.Caster.Type == UniversalHighGuid.Vehicle;
                var seen = holder.BaseData?.Time?.ToDateTime();
                var castId = data.CastGuid != null ? GuidKey(data.CastGuid) : null;
                if (castId == null && data.CastId != 0)
                    castId = $"{casterKey}:{data.Spell}:{data.CastId}";

                if (casterIsCreature && casterKey != null && units.TryGetValue(casterKey, out var caster))
                {
                    if (start != null && (castId == null || seenStarts.Add(castId)))
                    {
                        var row = new CreatureSpellCastRecord
                        {
                            SniffId = sniffId,
                            Guid = casterKey,
                            Entry = caster.Entry,
                            Map = caster.Map,
                            SpellId = data.Spell,
                            StartedUtc = seen
                        };

                        casts.Add(row);
                        if (castId != null)
                            startByCast[castId] = row;
                    }
                    else if (go != null && castId != null && startByCast.TryGetValue(castId, out var started))
                    {
                        started.Completed = true;
                    }
                }

                // Targets come off a completed cast only: an interrupted start never landed on
                // anything, and its target list is what the caster meant rather than what it hit.
                if (go != null)
                {
                    foreach (var hit in go.HitTargets)
                    {
                        if (hit == null || hit.Entry == 0)
                            continue;
                        if (hit.Type != UniversalHighGuid.Creature && hit.Type != UniversalHighGuid.Vehicle &&
                            hit.Type != UniversalHighGuid.GameObject)
                            continue;

                        var casterEntry = data.Caster.Entry;
                        var tkey = (data.Spell, casterEntry, hit.Entry);
                        if (!targetHits.TryGetValue(tkey, out var row))
                        {
                            targetHits[tkey] = row = new SpellTargetRecord
                            {
                                SniffId = sniffId,
                                SpellId = data.Spell,
                                CasterEntry = casterEntry,
                                CasterType = data.Caster.Type.ToString(),
                                TargetEntry = hit.Entry,
                                TargetType = hit.Type.ToString()
                            };
                        }

                        row.Hits++;
                    }
                }

                var dst = data.DstLocation;
                if (dst != null && !(dst.X == 0 && dst.Y == 0 && dst.Z == 0))
                {
                    uint? map = null;
                    if (casterKey != null && units.TryGetValue(casterKey, out var casterUnit))
                        map = casterUnit.Map;
                    else if (data.TargetUnit != null)
                    {
                        var targetKey = GuidKey(data.TargetUnit);
                        if (targetKey != null && units.TryGetValue(targetKey, out var targetUnit))
                            map = targetUnit.Map;
                    }

                    // Without a map the coordinates cannot be placed, and a guess would be worse
                    // than the missing row.
                    if (map.HasValue)
                    {
                        var dkey = (data.Spell, data.Caster.Entry, dst.X, dst.Y, dst.Z);
                        if (!dests.TryGetValue(dkey, out var row))
                        {
                            dests[dkey] = row = new SpellDestinationRecord
                            {
                                SniffId = sniffId,
                                SpellId = data.Spell,
                                CasterEntry = data.Caster.Entry,
                                Map = map.Value,
                                PositionX = dst.X,
                                PositionY = dst.Y,
                                PositionZ = dst.Z,
                                Orientation = data.HasDstOrientation ? data.DstOrientation : null
                            };
                        }

                        row.Casts++;
                    }
                }
            }

            targets.AddRange(targetHits.Values);
            destinations.AddRange(dests.Values);
            return casts;
        }

        /// <summary>
        /// Which parsed packets are worth keeping in memory when the destination is the ingest
        /// database rather than a proto file.
        ///
        /// Everything else is dropped the moment it is parsed: a long capture holds millions of
        /// packets and keeping every holder costs gigabytes for data no collector reads. Adding
        /// a collector that reads a new packet kind means adding that kind here, and the symptom
        /// of forgetting is a collector that silently returns nothing.
        /// </summary>
        private static bool WantedByIngest(PacketHolder holder)
        {
            return holder.MonsterMove != null ||
                   holder.AiReaction != null ||
                   holder.SpellGo != null ||
                   holder.SpellStart != null ||
                   holder.GossipMessage != null ||
                   holder.NpcText != null ||
                   holder.NpcTextOld != null ||
                   holder.ClientAreaTrigger != null;
        }

        /// <summary>What each creature was drawn holding.</summary>
        private List<CreatureEquipRecord> CollectCreatureEquipment(ulong sniffId)
        {
            var equipment = new List<CreatureEquipRecord>();

            foreach (var pair in Storage.Objects)
            {
                var obj = pair.Value.Item1;
                if (obj.Type != ObjectType.Unit || obj is not Unit unit || unit.UnitData == null)
                    continue;

                var entry = obj.ObjectData?.EntryID;
                if (entry == null || entry == 0)
                    continue;

                CreatureEquipment gear;
                try
                {
                    gear = unit.GetEquipment();
                }
                catch (Exception)
                {
                    // VirtualItems is not present on every build; a creature without it simply
                    // has no equipment to report.
                    continue;
                }

                if (gear == null)
                    continue;

                equipment.Add(new CreatureEquipRecord
                {
                    SniffId = sniffId,
                    Guid = GuidKey(pair.Key),
                    Entry = (uint)entry,
                    Map = obj.Map,
                    ItemId1 = gear.ItemID1 ?? 0,
                    ItemId2 = gear.ItemID2 ?? 0,
                    ItemId3 = gear.ItemID3 ?? 0
                });
            }

            return equipment;
        }

        /// <summary>
        /// Auras seen on creatures. Auras present in the block that created the creature are
        /// flagged, because those are the ones a spawn is meant to start with - anything added
        /// later may be a player acting on it.
        /// </summary>
        private List<CreatureAuraRecord> CollectCreatureAuras(ulong sniffId)
        {
            var auras = new List<CreatureAuraRecord>();

            foreach (var pair in Storage.Objects)
            {
                var obj = pair.Value.Item1;
                if (obj.Type != ObjectType.Unit || obj is not Unit unit)
                    continue;

                var entry = obj.ObjectData?.EntryID;
                if (entry == null || entry == 0)
                    continue;

                var guid = GuidKey(pair.Key);
                var bySpell = new Dictionary<uint, CreatureAuraRecord>();

                void Record(Aura aura, bool onCreate)
                {
                    if (aura == null || aura.SpellId == 0)
                        return;

                    if (!bySpell.TryGetValue(aura.SpellId, out var row))
                    {
                        bySpell[aura.SpellId] = row = new CreatureAuraRecord
                        {
                            SniffId = sniffId,
                            Guid = guid,
                            Entry = (uint)entry,
                            Map = obj.Map,
                            SpellId = aura.SpellId,
                            SelfCast = aura.CasterGuid == null || aura.CasterGuid.IsEmpty() ||
                                       aura.CasterGuid == pair.Key
                        };
                    }

                    row.Observations++;
                    if (onCreate)
                        row.OnCreate = true;
                    if (aura.MaxDuration != 0 && (row.MaxDurationMs == null || aura.MaxDuration > row.MaxDurationMs))
                        row.MaxDurationMs = aura.MaxDuration;
                }

                if (unit.Auras != null)
                {
                    foreach (var aura in unit.Auras)
                        Record(aura, true);
                }

                if (unit.AddedAuras != null)
                {
                    foreach (var list in unit.AddedAuras)
                    {
                        if (list == null)
                            continue;
                        foreach (var aura in list)
                            Record(aura, false);
                    }
                }

                auras.AddRange(bySpell.Values);
            }

            return auras;
        }

        /// <summary>Gossip menus, their options and the texts they point at.</summary>
        private List<GossipMenuRecord> CollectGossip(ulong sniffId, Packets packets,
                                                     out List<GossipMenuOptionRecord> options,
                                                     out List<NpcTextRecord> texts)
        {
            var menus = new List<GossipMenuRecord>();
            options = new List<GossipMenuOptionRecord>();
            texts = new List<NpcTextRecord>();

            if (packets == null)
                return menus;

            var seenMenus = new Dictionary<(uint Menu, uint Text, uint Entry), GossipMenuRecord>();
            var seenOptions = new HashSet<(uint Menu, uint Index)>();
            var seenTexts = new HashSet<(uint Text, int Slot)>();

            foreach (var holder in packets.Packets_)
            {
                var gossip = holder.GossipMessage;
                if (gossip != null)
                {
                    var entry = gossip.GossipSource?.Entry ?? 0;
                    var key = (gossip.MenuId, gossip.TextId, entry);
                    if (!seenMenus.TryGetValue(key, out var menu))
                    {
                        seenMenus[key] = menu = new GossipMenuRecord
                        {
                            SniffId = sniffId,
                            MenuId = gossip.MenuId,
                            TextId = gossip.TextId,
                            CreatureEntry = entry
                        };
                        menus.Add(menu);
                    }

                    menu.Observations++;

                    foreach (var option in gossip.Options)
                    {
                        if (!seenOptions.Add((gossip.MenuId, option.OptionIndex)))
                            continue;

                        options.Add(new GossipMenuOptionRecord
                        {
                            SniffId = sniffId,
                            MenuId = gossip.MenuId,
                            OptionIndex = option.OptionIndex,
                            OptionIcon = option.OptionNpc,
                            OptionText = option.Text,
                            BoxMoney = option.BoxCost,
                            BoxCoded = option.BoxCoded,
                            BoxText = option.BoxText
                        });
                    }
                }

                var old = holder.NpcTextOld;
                if (old != null)
                {
                    for (var i = 0; i < old.Texts.Count; i++)
                    {
                        if (!seenTexts.Add((old.Entry, i)))
                            continue;

                        var t = old.Texts[i];
                        texts.Add(new NpcTextRecord
                        {
                            SniffId = sniffId,
                            TextId = old.Entry,
                            Slot = i,
                            Probability = t.Probability,
                            Text0 = t.Text0,
                            Text1 = t.Text1,
                            Language = (uint)t.Language,
                            BroadcastTextId = 0
                        });
                    }
                }

                var modern = holder.NpcText;
                if (modern != null)
                {
                    for (var i = 0; i < modern.Texts.Count; i++)
                    {
                        if (!seenTexts.Add((modern.Entry, i)))
                            continue;

                        var t = modern.Texts[i];
                        texts.Add(new NpcTextRecord
                        {
                            SniffId = sniffId,
                            TextId = modern.Entry,
                            Slot = i,
                            Probability = t.Probability,
                            Text0 = null,
                            Text1 = null,
                            Language = 0,
                            BroadcastTextId = t.BroadcastTextId
                        });
                    }
                }
            }

            return menus;
        }

        /// <summary>
        /// Area triggers paired with the world change they caused.
        ///
        /// Nothing in a capture states that a teleport came from a trigger: the client reports
        /// crossing one and the server answers with a new world some packets later. The pairing
        /// is therefore by adjacency, and the delay is written down rather than assumed away -
        /// a trigger followed within a second or two is a teleport, a trigger followed a minute
        /// later is the player having walked to a portal in between. Filter on delay_ms.
        /// </summary>
        private List<AreaTriggerTeleportRecord> CollectAreaTriggerTeleports(ulong sniffId, Packets packets)
        {
            var teleports = new List<AreaTriggerTeleportRecord>();
            if (packets == null || MovementHandler.WorldPorts.Count == 0)
                return teleports;

            // The window a teleport has to land in. Generous enough for a loading screen on a
            // slow disk, short enough that an unrelated later port cannot be attributed here.
            var window = TimeSpan.FromSeconds(30);

            var ports = MovementHandler.WorldPorts;
            var next = 0;

            foreach (var holder in packets.Packets_)
            {
                var trigger = holder.ClientAreaTrigger;
                if (trigger == null || !trigger.Enter)
                    continue;

                var time = holder.BaseData?.Time?.ToDateTime();
                if (time == null)
                    continue;

                while (next < ports.Count && ports[next].Time < time.Value)
                    next++;

                if (next >= ports.Count)
                    break;

                var port = ports[next];
                var delay = port.Time - time.Value;
                if (delay > window)
                    continue;

                var from = next > 0 ? ports[next - 1] : null;

                teleports.Add(new AreaTriggerTeleportRecord
                {
                    SniffId = sniffId,
                    AreaTriggerId = trigger.AreaTrigger,
                    FromMap = from?.Map ?? 0,
                    FromX = from?.PositionX ?? 0,
                    FromY = from?.PositionY ?? 0,
                    FromZ = from?.PositionZ ?? 0,
                    ToMap = port.Map,
                    ToX = port.PositionX,
                    ToY = port.PositionY,
                    ToZ = port.PositionZ,
                    ToOrientation = port.Orientation,
                    DelayMs = (int)delay.TotalMilliseconds,
                    SeenUtc = time
                });
            }

            return teleports;
        }

        private List<CreatureSpawnRecord> CollectCreatureSpawns(ulong sniffId)
        {
            var spawns = new List<CreatureSpawnRecord>();

            foreach (var pair in Storage.Objects)
            {
                var obj = pair.Value.Item1;
                if (obj.Type != ObjectType.Unit || obj.IsTemporarySpawn())
                    continue;

                if (obj is not Unit unit)
                    continue;

                var entry = obj.ObjectData.EntryID;
                if (entry == null || entry == 0)
                    continue;

                // Dead when we saw it. Health is the signal that works across every build here;
                // the lootable dynamic flag agrees but is not set on every corpse.
                if (unit.UnitData.Health is <= 0)
                    continue;

                var pos = obj.Movement.Position;
                if (pos.X == 0 && pos.Y == 0 && pos.Z == 0)
                    continue;

                spawns.Add(new CreatureSpawnRecord
                {
                    SniffId = sniffId,
                    Guid = $"0x{pair.Key.High:X16}{pair.Key.Low:X16}",
                    Entry = (uint)entry,
                    Map = obj.Map,
                    AreaId = obj.Area != -1 ? obj.Area : null,
                    ZoneId = obj.Zone != -1 ? obj.Zone : null,
                    PositionX = pos.X,
                    PositionY = pos.Y,
                    PositionZ = pos.Z,
                    Orientation = obj.Movement.Orientation,
                    CreateType = (int)obj.CreateType,
                    PhaseMask = obj.PhaseMask != 0 ? obj.PhaseMask : null,
                    Phases = obj.Phases != null && obj.Phases.Count > 0 ? string.Join(" - ", obj.Phases) : null,
                    Level = unit.UnitData.Level,
                    FactionTemplate = unit.UnitData.FactionTemplate,
                    UnitFlags = unit.UnitData.Flags,
                    Health = unit.UnitData.Health,
                    EmoteState = unit.UnitData.EmoteState,
                    StandState = unit.UnitData.StandState,
                    SheatheState = unit.UnitData.SheatheState,
                    FirstSeenUtc = _firstPacketTimeUtc.HasValue && pair.Value.Item2.HasValue
                        ? _firstPacketTimeUtc.Value + pair.Value.Item2.Value
                        : _firstPacketTimeUtc
                });
            }

            return spawns;
        }

        private static string GuidKey(WowGuid guid) => $"0x{guid.High:X16}{guid.Low:X16}";

        private static string GuidKey(UniversalGuid guid)
        {
            if (guid == null)
                return null;
            if (guid.Guid128 != null)
                return $"0x{guid.Guid128.High:X16}{guid.Guid128.Low:X16}";
            if (guid.Guid64 != null)
                return $"0x{guid.Guid64.High:X16}{guid.Guid64.Low:X16}";
            return null;
        }

        /// <summary>
        /// Real waypoints for creatures that stayed out of combat.
        ///
        /// Two things are deliberately thrown away rather than flagged. The packedPoints array is
        /// delta compressed pathfinding filler, not server authored path, so it never becomes a
        /// waypoint. And every segment belonging to a creature that aggroed anywhere in this
        /// sniff is dropped, because chase and evade movement is not what the creature does when
        /// left alone - which also removes almost all of the volume.
        /// </summary>
        private List<CreatureWaypointRecord> CollectCreatureWaypoints(ulong sniffId, Packets packets)
        {
            var waypoints = new List<CreatureWaypointRecord>();
            if (packets == null)
                return waypoints;

            // Monster move packets carry no map, so take it from the object we saw move.
            var maps = new Dictionary<string, uint>();
            foreach (var pair in Storage.Objects)
            {
                if (pair.Value.Item1.Type == ObjectType.Unit)
                    maps[GuidKey(pair.Key)] = pair.Value.Item1.Map;
            }

            var aggroed = new HashSet<string>();
            foreach (var holder in packets.Packets_)
            {
                if (holder.AiReaction != null && holder.AiReaction.Reaction == Proto.AIReaction.Hostile)
                {
                    var key = GuidKey(holder.AiReaction.UnitGuid);
                    if (key != null)
                        aggroed.Add(key);
                }
            }

            var segmentId = 0;

            // Where each creature's most recently recorded point sits in the list, so a facing
            // that arrives on its own afterwards can be attached to it.
            var lastPointIndex = new Dictionary<string, int>();

            foreach (var holder in packets.Packets_)
            {
                var move = holder.MonsterMove;
                if (move?.Mover == null || move.Mover.Type != UniversalHighGuid.Creature)
                    continue;

                var key = GuidKey(move.Mover);
                if (key == null || aggroed.Contains(key) || !maps.TryGetValue(key, out var map))
                    continue;

                // A spline read out of a CreateObject block sets Holder.MonsterMove too, so the
                // opcode is what tells the two apart. Nothing in the parser ever sets the proto's
                // creationSpline flag, and the distinction matters: a create block states its
                // Destination outright, while SMSG_ON_MONSTER_MOVE leaves that field unset in
                // these builds and its real destination is simply the last point.
                var fromCreateObject = holder.BaseData?.Opcode != null &&
                                       holder.BaseData.Opcode.Contains("UPDATE_OBJECT");

                var points = new List<Vec3>(move.Points);

                float? finalOrientation = move.HasLookOrientation ? move.LookOrientation : null;

                if (move.Destination != null &&
                    !(move.Destination.X == 0 && move.Destination.Y == 0 && move.Destination.Z == 0))
                {
                    // The create block lists Destination separately from Points; a move packet
                    // that already ends there would otherwise get the same point twice.
                    var last = points.Count > 0 ? points[points.Count - 1] : null;
                    if (last == null || last.X != move.Destination.X ||
                        last.Y != move.Destination.Y || last.Z != move.Destination.Z)
                        points.Add(move.Destination);
                }

                // A facing can arrive in a movement packet of its own, carrying no points at all -
                // the server turning a creature in place once it has arrived somewhere. That
                // orientation belongs to the point it is standing on, which is the last one we
                // recorded for it.
                if (points.Count == 0)
                {
                    if (finalOrientation.HasValue && lastPointIndex.TryGetValue(key, out var standing))
                        waypoints[standing].Orientation = finalOrientation;
                    continue;
                }

                segmentId++;
                var seen = holder.BaseData?.Time?.ToDateTime();

                for (var i = 0; i < points.Count; i++)
                {
                    waypoints.Add(new CreatureWaypointRecord
                    {
                        SniffId = sniffId,
                        Guid = key,
                        Entry = move.Mover.Entry,
                        Map = map,
                        SegmentId = segmentId,
                        PointIndex = i,
                        SegmentPoints = points.Count,
                        PositionX = points[i].X,
                        PositionY = points[i].Y,
                        PositionZ = points[i].Z,
                        Orientation = i == points.Count - 1 ? finalOrientation : null,
                        SplineFlags = (uint)move.Flags,
                        CreationSpline = fromCreateObject,
                        MoveTimeMs = move.MoveTime != 0 ? move.MoveTime : null,
                        SeenUtc = seen
                    });
                }

                lastPointIndex[key] = waypoints.Count - 1;
            }

            return waypoints;
        }

        /// <summary>
        /// Loot taken while the player was on their own.
        ///
        /// Grouped loot is discarded rather than flagged: rolls, round robin and master loot all
        /// mean the response no longer lists everything the corpse held, which would quietly
        /// corrupt any chance worked out later. Party size is known from timestamped events, since
        /// packets are parsed in parallel and a running flag would depend on thread order.
        ///
        /// A sniff that begins with the player already grouped and never sees a party update is
        /// treated as solo - the first update usually arrives on login, but it is a real gap.
        /// </summary>
        private List<LootInstanceRecord> CollectLootInstances(ulong sniffId)
        {
            var loots = new List<LootInstanceRecord>(Storage.LootInstances);
            if (loots.Count == 0)
                return loots;

            loots.Sort((a, b) => Nullable.Compare(a.SeenUtc, b.SeenUtc));

            var partyStates = new List<PartyStateRecord>(Storage.PartyStates);
            partyStates.Sort((a, b) => a.Time.CompareTo(b.Time));

            var objects = new Dictionary<string, WoWObject>();
            foreach (var pair in Storage.Objects)
                objects[GuidKey(pair.Key)] = pair.Value.Item1;

            var kept = new List<LootInstanceRecord>();

            foreach (var loot in loots)
            {
                if (WasGrouped(partyStates, loot.SeenUtc))
                    continue;

                loot.SniffId = sniffId;

                if (loot.OwnerGuid != null && objects.TryGetValue(loot.OwnerGuid, out var owner))
                {
                    loot.OwnerEntry = owner.ObjectData?.EntryID is > 0 ? (uint?)owner.ObjectData.EntryID : null;
                    loot.OwnerType = owner.Type.ToString();
                    loot.Map = owner.Map;

                    if (owner is Unit unit)
                        loot.OwnerLevel = unit.UnitData.Level;
                }

                kept.Add(loot);
            }

            return kept;
        }

        private static bool WasGrouped(List<PartyStateRecord> states, DateTime? when)
        {
            if (states.Count == 0 || !when.HasValue)
                return false;

            var grouped = false;
            foreach (var state in states)
            {
                if (state.Time > when.Value)
                    break;
                grouped = state.PlayerCount > 1;
            }

            return grouped;
        }

        /// <summary>
        /// Records this file in the ingest database. The content hash is the identity: renaming a
        /// sniff, or receiving the same one twice through different archives, updates one row
        /// rather than piling up duplicates.
        /// </summary>
        private ulong SaveSniffRow()
        {
            var readFileName = _compression != FileCompression.None ? _tempName : FileName;
            var meta = _sniffMetadata ?? new SniffMetadata();

            var sniff = new SniffRecord
            {
                FileHash = HashFile(readFileName),
                FileName = Path.GetFileName(FileName),
                FileSize = new FileInfo(readFileName).Length,
                Sniffer = meta.SnifferName,
                SnifferId = meta.SnifferId != 0 ? meta.SnifferId : null,
                SnifferVersion = meta.SnifferVersion != 0 ? meta.SnifferVersion : null,
                PktVersion = meta.PktVersion,
                ClientBuild = ClientVersion.IsUndefined() ? null : (int)ClientVersion.Build,
                ClientVersion = ClientVersion.IsUndefined() ? null : ClientVersion.VersionString,
                ClientLocale = Misc.ClientLocale.PacketLocale.ToString(),
                Branch = ClientVersion.Branch.ToString(),
                HeaderStartUtc = meta.HeaderStartTimeUtc,
                FirstPacketUtc = _firstPacketTimeUtc,
                LastPacketUtc = _lastPacketTimeUtc,
                UtcOffsetSeconds = meta.UtcTimeOffsetSeconds,
                ClockSkewSeconds = ClockSkew(meta.HeaderStartTimeUtc, _firstPacketTimeUtc),
                PacketCount = _packetsRead,
                ParsedCount = _stats.SuccessPacketCount,
                ErrorCount = _stats.WithErrorsPacketCount,
                SkippedCount = _stats.NotParsedPacketCount,
                NoStructureCount = _stats.NoStructurePacketCount,
                StructureVersion = (uint)StructureVersion.ProtobufStructureVersion
            };

            var id = IngestDatabase.SaveSniff(sniff);
            if (id == 0)
            {
                Trace.WriteLine($"{_logPrefix}: Sniff was NOT recorded in the ingest database");
                return 0;
            }

            var offset = sniff.UtcOffsetSeconds.HasValue
                ? $"UTC{sniff.UtcOffsetSeconds.Value / 3600.0:+0.#;-0.#;+0}"
                : "unknown";
            Trace.WriteLine($"{_logPrefix}: Recorded as sniff #{id} " +
                            $"({sniff.FirstPacketUtc:yyyy-MM-dd HH:mm:ss} to {sniff.LastPacketUtc:HH:mm:ss} UTC, " +
                            $"capture clock {offset})");
            return id;
        }

        /// <summary>
        /// Gap between the header clock and the packet clock. Anything past half an hour is not
        /// drift - it means the two were written in different time zones, so times out of this
        /// sniff cannot be lined up against another contributor's without correcting for it.
        /// </summary>
        private int? ClockSkew(DateTime? headerStart, DateTime? firstPacket)
        {
            if (!headerStart.HasValue || !firstPacket.HasValue)
                return null;

            var skew = (int)(firstPacket.Value - headerStart.Value).TotalSeconds;
            if (Math.Abs(skew) > 1800)
                Trace.WriteLine($"{_logPrefix}: WARNING - packet times sit {skew / 3600.0:0.##} h from the " +
                                "header start time; this sniff's clock disagrees with itself");
            return skew;
        }

        private static string HashFile(string fileName)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(fileName))
                return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        private void WriteSQLs(Packets packets)
        {
            var sqlFileName = string.IsNullOrWhiteSpace(Settings.SQLFileName) ? $"{Utilities.FormattedDateTimeForFiles()}_{Path.GetFileName(FileName)}.sql" : Settings.SQLFileName;

            if (!string.IsNullOrWhiteSpace(Settings.SQLFileName))
                return;

            Builder.DumpSQL(new []{packets}, $"{_logPrefix}: Dumping sql", sqlFileName, GetHeader(FileName));
            Storage.ClearContainers();
        }

        private void WritePacketErrors()
        {
            if (_withErrorHeaders.Count == 0 && _skippedHeaders.Count == 0 && _noStructureHeaders.Count == 0)
                return;

            var fileName = Path.GetFileNameWithoutExtension(FileName) + "_errors.txt";

            using (var file = new StreamWriter(fileName))
            {
                file.WriteLine(GetHeader(FileName));

                if (_withErrorHeaders.Count != 0)
                {
                    file.WriteLine("- Packets with errors:");
                    foreach (var header in _withErrorHeaders)
                        file.WriteLine(header);
                    file.WriteLine();
                }

                if (_skippedHeaders.Count != 0)
                {
                    file.WriteLine("- Packets not parsed:");
                    foreach (var header in _skippedHeaders)
                        file.WriteLine(header);
                    file.WriteLine();
                }

                if (_noStructureHeaders.Count != 0)
                {
                    file.WriteLine("- Packets without structure:");
                    foreach (var header in _noStructureHeaders)
                        file.WriteLine(header);
                }
            }
        }

        private void Compress()
        {
            var fileToCompress = new FileInfo(FileName);
            _compression = FileCompression.GZip;

            using (var originalFileStream = fileToCompress.OpenRead())
            {
                using (var compressedFileStream = File.Create(GetCompressedFileName()))
                {
                    using (var compressionStream = new GZipStream(compressedFileStream, CompressionMode.Compress, true))
                    {
                        originalFileStream.CopyTo(compressionStream);
                    }

                    Trace.WriteLine($"{_logPrefix} Compressed {fileToCompress.Name} from {fileToCompress.Length} to {compressedFileStream.Length} bytes.");
                }
            }
        }

        public string Decompress()
        {
            var fileToDecompress = new FileInfo(GetCompressedFileName());

            using (var originalFileStream = fileToDecompress.OpenRead())
            {
                var newFileName = Path.GetTempFileName();

                using (var decompressedFileStream = File.Create(newFileName))
                {
                    switch (_compression)
                    {
                        case FileCompression.GZip:
                            using (var decompressionStream = new GZipStream(originalFileStream, CompressionMode.Decompress))
                            {
                                decompressionStream.CopyTo(decompressedFileStream);
                            }
                            break;
                        default:
                            throw new NotImplementedException($"Invalid decompression method for {fileToDecompress.Name}");
                    }
                }

                Trace.WriteLine($"{_logPrefix} Decompressed {fileToDecompress.Name} to {Path.GetFileName(newFileName)}");
                return newFileName;
            }
        }

        private string GetCompressedFileName()
        {
            return FileName + _compression.GetExtension();
        }
    }
}
