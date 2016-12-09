﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Threading;
using TsDecoder.TransportStream;

namespace TsAnalyser.Metrics
{
    [DataContract]
    public class PidMetric : Metric
    {
        public delegate void DiscontinuityDetectedEventHandler(object sender, TransportStreamEventArgs args);
        public delegate void TransportErrorIndicatorDetectedEventHandler(object sender, TransportStreamEventArgs args);

        private int _periodPacketCount = 0;
        private int _periodCcErrorCount = 0;
        private int _periodTeiCount = 0;
        private bool _hasPcr = false;
        private ulong _lastPcr = 0;
        private ulong _periodLargestPcrDelta;
        private int _periodLargestPcrDrift;

        private ulong _referencePcr;
        private ulong _referenceTime;

        internal override void ResetPeriodTimerCallback(object o)
        {
            lock (this)
            {
                PeriodPacketCount = _periodPacketCount;
                _periodPacketCount = 0;

                PeriodCcErrorCount = _periodCcErrorCount;
                _periodCcErrorCount = 0;

                PeriodTeiCount = _periodTeiCount;
                _periodTeiCount = 0;

                PeriodLargestPcrDelta = (int)new TimeSpan((long)(_periodLargestPcrDelta / 2.7)).TotalMilliseconds;

                _periodLargestPcrDelta = 0;

                PeriodLargestPcrDrift = _periodLargestPcrDrift;
                _periodLargestPcrDrift = 0;


                base.ResetPeriodTimerCallback(o);
            }
        }

        [DataMember]
        public int Pid { get; set; }

        [DataMember]
        public long PacketCount { get; private set; }
        
        [DataMember]
        public int PeriodPacketCount { get; private set; }

        public long TeiCount { get; private set; }

        [DataMember]
        public int PeriodTeiCount { get; private set; }

        [DataMember]
        public long CcErrorCount { get; private set; }

        [DataMember]
        public int PeriodCcErrorCount { get; private set; }

        [DataMember]
        public bool HasPcr => _hasPcr;

        [DataMember]
        public int PeriodLargestPcrDelta { get; private set; }


        [DataMember]
        public int PeriodLargestPcrDrift { get; private set; }

        private int LastCc { get; set; }
        
        public void AddPacket(TsPacket newPacket)
        {
            try
            {
                if (newPacket.Pid != Pid)
                    throw new InvalidOperationException("Cannot add TS Packet from different pid to a metric!");

                if (newPacket.TransportErrorIndicator)
                {
                    TeiCount++;
                    _periodTeiCount++;
                }
                else
                {
                    CheckCcContinuity(newPacket);
                    CheckPcr(newPacket);
                    LastCc = newPacket.ContinuityCounter;
                }

                PacketCount++;
                _periodPacketCount++;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Exception generated within AddPacket method: " + ex.Message);
            }
        }

        private void CheckPcr(TsPacket tsPacket)
        {
            if (!tsPacket.AdaptationFieldExists) return;
            if (!tsPacket.AdaptationField.PcrFlag) return;
            if (tsPacket.AdaptationField.FieldSize < 1) return;

            if (tsPacket.AdaptationField.DiscontinuityIndicator)
            {
                Debug.WriteLine("Adaptation field discont indicator");
                return;
            }
            
            if (_lastPcr != 0)
            {
                //if (tsPacket.AdaptationField.Pcr == 0)
                //{
                //    Debug.WriteLine("Why is this zero?");
                //}

                var latestDelta = tsPacket.AdaptationField.Pcr - _lastPcr;
                if (latestDelta > _periodLargestPcrDelta) _periodLargestPcrDelta = latestDelta;

                var elapsedPcr = (long)(tsPacket.AdaptationField.Pcr - _referencePcr);
                var elapsedClock = (long)((DateTime.UtcNow.Ticks * 2.7) - _referenceTime);
                var drift = (int)(elapsedClock - elapsedPcr) / 27000;
                if (drift > _periodLargestPcrDrift)
                {
                    _periodLargestPcrDrift = drift;
                }

                //if (drift > 1000 || drift < -1000)
                //{
                //    Debug.WriteLine($"Stupid drift value on pid {tsPacket.Pid}: {drift}");
                //}
            }
            else
            {
                //first PCR value - set up reference values
                _referencePcr = tsPacket.AdaptationField.Pcr;
                _referenceTime = (ulong)(DateTime.UtcNow.Ticks*2.7);
            }

            _lastPcr = tsPacket.AdaptationField.Pcr;
        }

        private void CheckCcContinuity(TsPacket newPacket)
        {
            try
            {
                if (PacketCount == 0)
                {
                    //fresh metric, first packet - so no possible error yet...
                    return;
                }

                if (newPacket.Pid == 0x1fff)
                    return;

                if (LastCc == newPacket.ContinuityCounter)
                {
                    if (newPacket.ContainsPayload)
                    {
                        CcErrorCount++;
                        _periodCcErrorCount++;
                    }

                    //special case of no data... ignore for now
                    //TODO: check for no data flag in original packet
                    Debug.Assert(true, "Special CC repeated case - requires investigation!");
                    return;
                }

                if (LastCc != 15)
                {
                    if (LastCc + 1 != newPacket.ContinuityCounter)
                    {
                        CcErrorCount++;
                        _periodCcErrorCount++;
                        OnDiscontinuityDetected(newPacket);
                        return;
                    }
                }

                if (LastCc != 15 || newPacket.ContinuityCounter == 0) return;

                CcErrorCount++;
                _periodCcErrorCount++;
                OnDiscontinuityDetected(newPacket);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Exception generated within CheckCcContinuity method: " + ex.Message);
            }
        }

        // Continuity Counter Error has been detected.
        public event DiscontinuityDetectedEventHandler DiscontinuityDetected;
        
        private void OnDiscontinuityDetected(TsPacket tsPacket)
        {
            //reset reference PCR values used for drift check - set up reference values
            _referencePcr = tsPacket.AdaptationField.Pcr;
            _referenceTime = (ulong)(DateTime.UtcNow.Ticks * 2.7);

            var handler = DiscontinuityDetected;
            if (handler == null) return;
            var args = new TransportStreamEventArgs { TsPid = tsPacket.Pid };
            handler(this, args);
        }
        
    }
}