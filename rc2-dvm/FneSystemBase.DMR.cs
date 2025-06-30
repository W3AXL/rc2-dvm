using Microsoft.VisualBasic;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;

using fnecore.DMR;
using fnecore;

using NAudio.Wave;
using fnecore.P25;

namespace rc2_dvm
{
    /// <summary>
    /// Implements a FNE system base.
    /// </summary>
    public abstract partial class FneSystemBase : fnecore.FneSystemBase
    {
        public const int AMBE_BUF_LEN = 9;
        public const int DMR_AMBE_LENGTH_BYTES = 27;
        public const int AMBE_PER_SLOT = 3;

        public const int P25_FIXED_SLOT = 2;

        public const int SAMPLE_RATE = 8000;
        public const int BITS_PER_SECOND = 16;

        public const int MBE_SAMPLES_LENGTH = 160;

        public const int AUDIO_BUFFER_MS = 20;
        public const int AUDIO_NO_BUFFERS = 2;
        public const int AFSK_AUDIO_BUFFER_MS = 60;
        public const int AFSK_AUDIO_NO_BUFFERS = 4;

        public new const int DMR_PACKET_SIZE = 55;
        public new const int DMR_FRAME_LENGTH_BYTES = 33;

        /*
        ** Methods
        */

        /// <summary>
        /// Callback used to validate incoming DMR data.
        /// </summary>
        /// <param name="peerId">Peer ID</param>
        /// <param name="srcId">Source Address</param>
        /// <param name="dstId">Destination Address</param>
        /// <param name="slot">Slot Number</param>
        /// <param name="callType">Call Type (Group or Private)</param>
        /// <param name="frameType">Frame Type</param>
        /// <param name="dataType">DMR Data Type</param>
        /// <param name="streamId">Stream ID</param>
        /// <param name="message">Raw message data</param>
        /// <returns>True, if data stream is valid, otherwise false.</returns>
        protected override bool DMRDataValidate(uint peerId, uint srcId, uint dstId, byte slot, fnecore.CallType callType, FrameType frameType, DMRDataType dataType, uint streamId, byte[] message)
        {
            return true;
        }

        /// <summary>
        /// Creates an DMR frame message.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="srcId"></param>
        /// <param name="dstId"></param>
        /// <param name="slot"></param>
        /// <param name="frameType"></param>
        /// <param name="seqNo"></param>
        /// <param name="n"></param>
        public void CreateDMRMessage(ref byte[] data, uint srcId, uint dstId, byte slot, FrameType frameType, byte seqNo, byte n)
        {
            RemoteCallData callData = new RemoteCallData()
            {
                SrcId = srcId,
                DstId = dstId,
                FrameType = frameType,
                Slot = slot,
            };

            CreateDMRMessage(ref data, callData, seqNo, n);
        }

        /// <summary>
        /// Helper to send a DMR terminator with LC message.
        /// </summary>
        public void SendDMRTerminator(uint srcId, uint dstId, byte slot, int dmrSeqNo, byte dmrN, EmbeddedData embeddedData)
        {
            RemoteCallData callData = new RemoteCallData()
            {
                SrcId = srcId,
                DstId = dstId,
                FrameType = FrameType.DATA_SYNC,
                Slot = slot
            };

            SendDMRTerminator(callData, ref dmrSeqNo, ref dmrN, embeddedData);
        }

        /// <summary>
        /// Event handler used to process incoming DMR data.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected override void DMRDataReceived(object sender, DMRDataReceivedEvent e)
        {
            DateTime pktTime = DateTime.Now;

            // Decode DMR frame data
            byte[] data = new byte[DMR_FRAME_LENGTH_BYTES];
            Buffer.BlockCopy(e.Data, 20, data, 0, DMR_FRAME_LENGTH_BYTES);
            byte bits = e.Data[15];

            // We only handle group calls right now
            if (e.CallType == fnecore.CallType.GROUP)
            {
                if (e.SrcId == 0)
                {
                    Log.Logger.Warning("({0:l}) DMRD: Received call from SRC_ID {1}? Dropping call data.", SystemName, e.SrcId);
                    return;
                }

                // Find any virtual channels which have this talkgroup/timeslot selected or ATG
                foreach (VirtualChannel channel in RC2DVM.VirtualChannels)
                {
                    // Don't send to channels that are transmitting
                    if (channel.IsTransmitting()) {
                        Log.Logger.Debug("({0:l}) Not sending data from DMR TG {1} to channel {2}, channel is currently transmitting", RC2DVM.fneSystem.SystemName, e.DstId, channel.Config.Name);
                        continue; 
                    }
                    // Send data to any channel on the TG or configured for the TG as its ATG
                    if (channel.IsTalkgroupSelected(VocoderMode.DMR, e.DstId, e.Slot) || (channel.Config.AnnouncementGroup == e.DstId))
                    {
                        Log.Logger.Debug("({0:l}) DMR TG {2} -> {3}", RC2DVM.fneSystem.SystemName, e.DstId, channel.Config.Name);
                        channel.DMRHandleRecieved(e, data, pktTime);
                    }
                    else if (channel.Config.AnnouncementGroup == e.DstId)
                    {
                        Log.Logger.Debug("({0:l}) DMR ATG {1} -> {2}", RC2DVM.fneSystem.SystemName, e.DstId, channel.Config.Name);
                        channel.DMRHandleRecieved(e, data, pktTime);
                    }
                }
            }

            return;
        }
    } // public abstract partial class FneSystemBase : fnecore.FneSystemBase
}
