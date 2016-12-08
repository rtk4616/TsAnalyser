﻿using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TsDecoder.TransportStream
{
    public struct PesHdr
    {
        public long Pts;
        public long Dts;
        public int StartCode;
        public byte[] Payload;
    }

    public struct TsPacket
    {
        public byte SyncByte; //should always be 0x47 - indicates start of a TS packet
        public bool TransportErrorIndicator; //Set when a demodulator can't correct errors from FEC data - this would inform a stream processor to ignore the packet
        public bool PayloadUnitStartIndicator; //true = the start of PES data or PSI otherwise zero only. 
        public bool TransportPriority; //true = the current packet has a higher priority than other packets with the same PID.
        public short Pid; //Packet identifier flag, used to associate one packet with a set
        public short ScramblingControl; // '00' = Not scrambled, For DVB-CSA only:'01' = Reserved for future use, '10' = Scrambled with even key, '11' = Scrambled with odd key
        public bool AdaptationFieldExists;
        public bool ContainsPayload;
        public short ContinuityCounter;
        public PesHdr PesHeader;
        public byte[] Payload;
        public AdaptationField AdaptationField;
    }

    public struct AdaptationField
    {
        public int FieldSize;
        public bool DiscontinuityIndicator;
        public bool RandomAccessIndicator;
        public bool ElementaryStreamPriorityIndicator;
        public bool PcrFlag;
        public bool OpcrFlag;
        public bool SplicingPointFlag;
        public bool TransportPrivateDataFlag;
        public bool AdaptationFieldExtensionFlag;
        public ulong Pcr;
    }

    public static class TsPacketFactory
    {
        private const byte SyncByte = 0x47;
        private const int TsPacketSize = 188;

        public static TsPacket[] GetTsPacketsFromData(byte[] data)
        {
            try
            {
                var maxPackets = (data.Length) / TsPacketSize;
                var tsPackets = new TsPacket[maxPackets];

                var start = FindSync(data, 0);
                var packetCounter = 0;

                while (start >= 0)
                {
                    var tsPacket = new TsPacket
                    {
                        SyncByte = data[start],
                        Pid = (short)(((data[start + 1] & 0x1F) << 8) + (data[start + 2])),
                        TransportErrorIndicator = (data[start + 1] & 0x80) != 0,
                        PayloadUnitStartIndicator = (data[start + 1] & 0x40) != 0,
                        TransportPriority = (data[start + 1] & 0x20) != 0,
                        ScramblingControl = (short)(data[start + 3] >> 6),
                        AdaptationFieldExists = (data[start + 3] & 0x20) != 0,
                        ContainsPayload = (data[start + 3] & 0x10) != 0,
                        ContinuityCounter = (short)(data[start + 3] & 0xF)
                    };

                    if (tsPacket.ContainsPayload && !tsPacket.TransportErrorIndicator && (tsPacket.Pid != 0x1fff))
                    {
                        var payloadOffs = start + 4;
                        var payloadSize = TsPacketSize - 4;

                        if (tsPacket.AdaptationFieldExists)
                        {
                            tsPacket.AdaptationField = new AdaptationField()
                            {
                                FieldSize = 1 + data[start + 4],
                                PcrFlag = (data[start + 5] & 0x10) !=0
                            };
                        

                        if (tsPacket.AdaptationField.FieldSize >= payloadSize)
                            {
                                Debug.WriteLine("TS packet data adaptationFieldSize >= payloadSize");
                                return null;
                            }
                        

                            if (tsPacket.AdaptationField.PcrFlag)
                            {
                                //Packet has PCR

                                var a = (uint)data[start + 6];
                                var b = (uint)(data[start + 7]);
                                var c = (uint)(data[start + 8]);
                                var d = (uint)(data[start + 9]);

                                var shifted = (a << 24);

                                tsPacket.AdaptationField.Pcr = (ulong)(((uint)(data[start + 6]) << 24) + ((uint)(data[start + 7] << 16)) + ((uint)(data[start + 8] << 8)) + ((uint)data[start + 9]));
                                tsPacket.AdaptationField.Pcr <<= 1;
                                if ((data[start + 10] & 0x80) == 1)
                                {
                                    tsPacket.AdaptationField.Pcr |= 1;
                                }
                                tsPacket.AdaptationField.Pcr *= 300;
                                var iLow = (uint)((data[start + 10] & 1) << 8) + data[start + 11];
                                tsPacket.AdaptationField.Pcr += iLow;
                            }

                            payloadSize -= tsPacket.AdaptationField.FieldSize;
                            payloadOffs += tsPacket.AdaptationField.FieldSize;
                        }

                        if (tsPacket.PayloadUnitStartIndicator)
                        {
                            if (payloadOffs > (data.Length - 2) || data[payloadOffs] != 0 || data[payloadOffs + 1] != 0 || data[payloadOffs + 2] != 1)
                            {
#if DEBUG
                            //    Debug.WriteLine("PES syntax error: no PES startcode found, or payload offset exceeds boundary of data");
#endif
                            }
                            else
                            {
                                tsPacket.PesHeader = new PesHdr
                                {
                                    StartCode = 0x100 + data[payloadOffs + 3],
                                    Pts = -1,
                                    Dts = -1
                                };

                                var ptsDtsFlag = data[payloadOffs + 7] >> 6;

                                switch (ptsDtsFlag)
                                {
                                    case 2:
                                        tsPacket.PesHeader.Pts = Get_TimeStamp(2, data, payloadOffs + 9);
                                        break;
                                    case 3:
                                        tsPacket.PesHeader.Pts = Get_TimeStamp(3, data, payloadOffs + 9);
                                        tsPacket.PesHeader.Dts = Get_TimeStamp(1, data, payloadOffs + 14);
                                        break;
                                    case 1:
                                        throw new Exception("PES Syntax error: pts_dts_flag = 1");
                                }

                                if (tsPacket.AdaptationField.PcrFlag && ptsDtsFlag > 1)
                                {
                                    var ts = new TimeSpan((long)(((long)tsPacket.AdaptationField.Pcr - tsPacket.PesHeader.Pts * 300)/2.7));
                                    Debug.WriteLine($"PCR: {tsPacket.AdaptationField.Pcr}, PTS: {tsPacket.PesHeader.Pts}, Delta = {ts}");
                                }

                                var pesLength = 9 + data[payloadOffs + 8];
                                tsPacket.PesHeader.Payload = new byte[pesLength];
                                Buffer.BlockCopy(data, payloadOffs, tsPacket.PesHeader.Payload, 0, pesLength);

                                payloadOffs += pesLength;
                                payloadSize -= pesLength;
                            }
                        }

                        if (payloadSize < 1)
                        {
                            tsPacket.TransportErrorIndicator = true;
                        }
                        else
                        {
                            tsPacket.Payload = new byte[payloadSize];
                            Buffer.BlockCopy(data, payloadOffs, tsPacket.Payload, 0, payloadSize);
                        }
                    }

                    tsPackets[packetCounter++] = tsPacket;

                    start += TsPacketSize;

                    if (start >= data.Length)
                        break;
                    if (data[start] != SyncByte)
                        break;  // but this is strange!
                }

                return tsPackets;
            }

            catch (Exception ex)
            {
                Debug.WriteLine("Exception within GetTsPacketsFromData method: " + ex.Message);
            }

            return null;
        }

        public static TimeSpan PcrToTimespan(ulong pcr)
        {
            var day = (int)(pcr / 2332800000000L);
            var r = pcr % 2332800000000L;
            var hour = (int)(r / (97200000000L));
            r = r % (97200000000L);
            var minute = (int)(r / (1620000000L));
            r = r % (1620000000L);
            var second = (int)(r / (27000000L));
            r = r % (27000000L);
            var millis = (int)(r / 27000);

            return new TimeSpan(day,hour,minute,second, millis);
        }

        private static long Get_TimeStamp(int code, IList<byte> data, int offs)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            if (code == 0)
            {
                Debug.WriteLine("Method has been called with incorrect code to match against - check for fault in calling method.");
                throw new Exception("PES Syntax error: 0 value timestamp code check passed in");
            }

            if ((data[offs + 0] >> 4) != code)
                throw new Exception("PES Syntax error: Wrong timestamp code");

            if ((data[offs + 0] & 1) != 1)
                throw new Exception("PES Syntax error: Invalid timestamp marker bit");

            if ((data[offs + 2] & 1) != 1)
                throw new Exception("PES Syntax error: Invalid timestamp marker bit");

            if ((data[offs + 4] & 1) != 1)
                throw new Exception("PES Syntax error: Invalid timestamp marker bit");

            long a = (data[offs + 0] >> 1) & 7;
            long b = (data[offs + 1] << 7) | (data[offs + 2] >> 1);
            long c = (data[offs + 3] << 7) | (data[offs + 4] >> 1);

            return (a << 30) | (b << 15) | c;
        }

        private static int FindSync(IList<byte> tsData, int offset)
        {
            if (tsData == null) throw new ArgumentNullException(nameof(tsData));
            
            //not big enough to be any kind of single TS packet
            if (tsData.Count < 188)
            {
                return -1;
            }

            try
            {
                for (var i = offset; i < tsData.Count; i++)
                {
                    //check to see if we found a sync byte
                    if (tsData[i] != SyncByte) continue;
                    if (i + 1 * TsPacketSize < tsData.Count && tsData[i + 1 * TsPacketSize] != SyncByte) continue;
                    if (i + 2 * TsPacketSize < tsData.Count && tsData[i + 2 * TsPacketSize] != SyncByte) continue;
                    if (i + 3 * TsPacketSize < tsData.Count && tsData[i + 3 * TsPacketSize] != SyncByte) continue;
                    if (i + 4 * TsPacketSize < tsData.Count && tsData[i + 4 * TsPacketSize] != SyncByte) continue;
                    // seems to be ok
                    return i;
                }
                return -1;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Problem in FindSync algorithm... : ", ex.Message);
                throw;
            }
        }
    }

    public enum PidType
    {
        PatPid = 0x0,
        CatPid = 0x1,
        TsDescPid = 0x2,
        NitPid = 0x10,
        SdtPid = 0x11,
        NullPid = 0x1FFF
    }
}
