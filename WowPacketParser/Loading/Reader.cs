using System;
using System.Diagnostics;
using System.Text;
using WowPacketParser.Enums;
using WowPacketParser.Enums.Version;
using WowPacketParser.Misc;

namespace WowPacketParser.Loading
{
    public class Reader
    {
        public string FileName { get; }
        public IPacketReader PacketReader { get; }

        public Reader(string fileName, SniffType type)
        {
            FileName = fileName;
            PacketReader = GetPacketReader(fileName, type);
        }

        private static IPacketReader GetPacketReader(string fileName, SniffType type)
        {
            switch (type)
            {
                case SniffType.Sqlite:
                    return new SqLitePacketReader(type, fileName, Encoding.ASCII);
                default: // .pkt
                    return new BinaryPacketReader(type, fileName, Encoding.ASCII);
            }
        }

        /// <summary>A read failed; nothing after it can be framed, so reading should stop.</summary>
        public bool Broken { get; private set; }

        private int _packetNum;
        private int _count;

        public bool TryRead(out Packet packet)
        {
            try
            {
                packet = PacketReader.Read(_packetNum++, FileName);
                if (packet == null)
                    return false; // continue
                
                // check for filters
                var opcodeName = Opcodes.GetOpcodeName(packet.Opcode, packet.Direction);

                var add = true;
                if (Settings.Filters.Length > 0)
                    add = opcodeName.MatchesFilters(Settings.Filters);
                // check for ignore filters
                if (add && Settings.IgnoreFilters.Length > 0)
                    add = !opcodeName.MatchesFilters(Settings.IgnoreFilters);

                if (add)
                {
                    if (Settings.FilterPacketsNum > 0 && _count++ == Settings.FilterPacketsNum)
                        return true; // break
                    return false; // continue
                }

                packet.ClosePacket();
                packet = null;
                return false;
            }
            catch (Exception ex)
            {
                // A packet that cannot be read leaves the stream mid-record, and every "packet"
                // after it is misframed garbage. Carrying on steps through the rest of the file a
                // few bytes at a time - one corrupt 37 MB capture threw 3.1 million exceptions and
                // 845 MB of trace this way - so stop reading the file here instead.
                Broken = true;
                Trace.WriteLine($"Packet {_packetNum} of {FileName} could not be read ({ex.GetType().Name}: {ex.Message}) - the rest of the file is unreadable, stopping here");
            }

            packet = null;
            return false;
        }

        public static void Read(string fileName, SniffType type, Action<Tuple<Packet, long, long>> action)
        {
            var reader = GetPacketReader(fileName, type);

            try
            {
                int packetNum = 0, count = 0;
                while (reader.CanRead())
                {
                    var packet = reader.Read(packetNum++, fileName);
                    if (packet == null)
                        continue;

                    // check for filters
                    var opcodeName = Opcodes.GetOpcodeName(packet.Opcode, packet.Direction);

                    var add = true;
                    if (Settings.Filters.Length > 0)
                        add = opcodeName.MatchesFilters(Settings.Filters);
                    // check for ignore filters
                    if (add && Settings.IgnoreFilters.Length > 0)
                        add = !opcodeName.MatchesFilters(Settings.IgnoreFilters);

                    if (add)
                    {
                        action(Tuple.Create(packet, reader.GetCurrentSize(), reader.GetTotalSize()));
                        if (Settings.FilterPacketsNum > 0 && count++ == Settings.FilterPacketsNum)
                            break;
                    }
                    else
                        packet.ClosePacket();
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.Data);
                Trace.WriteLine(ex.GetType());
                Trace.WriteLine(ex.Message);
                Trace.WriteLine(ex.StackTrace);
            }
            finally
            {
                reader.Dispose();
            }
        }
    }
}
