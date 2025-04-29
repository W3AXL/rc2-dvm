using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Linq;

using Serilog;

using fnecore;
using fnecore.DMR;
using Microsoft.VisualBasic;
using SIPSorcery.Media;
using SIPSorcery.Sys;
using NAudio.Wave;
using fnecore.P25.KMM;

namespace rc2_dvm
{
    /// <summary>
    /// Represents the individual timeslot data status.
    /// </summary>
    public class SlotStatus
    {
        /// <summary>
        /// Rx Start Time
        /// </summary>
        public DateTime RxStart = DateTime.Now;

        /// <summary>
        /// 
        /// </summary>
        public uint RxSeq = 0;

        /// <summary>
        /// Rx RF Source
        /// </summary>
        public uint RxRFS = 0;
        /// <summary>
        /// Tx RF Source
        /// </summary>
        public uint TxRFS = 0;

        /// <summary>
        /// Rx Stream ID
        /// </summary>
        public uint RxStreamId = 0;
        /// <summary>
        /// Tx Stream ID
        /// </summary>
        public uint TxStreamId = 0;

        /// <summary>
        /// Rx TG ID
        /// </summary>
        public uint RxTGId = 0;
        /// <summary>
        /// Tx TG ID
        /// </summary>
        public uint TxTGId = 0;
        /// <summary>
        /// Tx Privacy TG ID
        /// </summary>
        public uint TxPITGId = 0;

        /// <summary>
        /// Rx Time
        /// </summary>
        public DateTime RxTime = DateTime.Now;
        /// <summary>
        /// Tx Time
        /// </summary>
        public DateTime TxTime = DateTime.Now;

        /// <summary>
        /// Rx Type
        /// </summary>
        public FrameType RxType = FrameType.TERMINATOR;

        /** DMR Data */
        /// <summary>
        /// Rx Link Control Header
        /// </summary>
        public LC DMR_RxLC = null;
        /// <summary>
        /// Rx Privacy Indicator Link Control Header
        /// </summary>
        public PrivacyLC DMR_RxPILC = null;
        /// <summary>
        /// Tx Link Control Header
        /// </summary>
        public LC DMR_TxHLC = null;
        /// <summary>
        /// Tx Privacy Link Control Header
        /// </summary>
        public PrivacyLC DMR_TxPILC = null;
        /// <summary>
        /// Tx Terminator Link Control
        /// </summary>
        public LC DMR_TxTLC = null;
    } // public class SlotStatus

    /// <summary>
    /// Implements a FNE system.
    /// </summary>
    public abstract partial class FneSystemBase : fnecore.FneSystemBase
    {
        public abstract Task StartListeningAsync();

        

#if WIN32
        private AmbeVocoder extFullRateVocoder;
        private AmbeVocoder extHalfRateVocoder;
#endif

        private Random rand;

        // List of active calls
        private List<(uint, byte)> activeTalkgroups = new List<(uint, byte)>();

        /*
        ** Methods
        */

        /// <summary>
        /// Initializes a new instance of the <see cref="FneSystemBase"/> class.
        /// </summary>
        /// <param name="fne">Instance of <see cref="FneMaster"/> or <see cref="FnePeer"/></param>
        public FneSystemBase(FnePeer fne) : base(fne, RC2DVM.FneLogLevel)
        {
            this.fne = fne;

            this.rand = new Random(Guid.NewGuid().GetHashCode());

            // hook logger callback
            this.fne.Logger = (LogLevel level, string message) =>
            {
                switch (level)
                {
                    case LogLevel.WARNING:
                        Log.Logger.Warning(message);
                        break;
                    case LogLevel.ERROR:
                        Log.Logger.Error(message);
                        break;
                    case LogLevel.DEBUG:
                        Log.Logger.Debug(message);
                        break;
                    case LogLevel.FATAL:
                        Log.Logger.Fatal(message);
                        break;
                    case LogLevel.INFO:
                    default:
                        Log.Logger.Information(message);
                        break;
                }
            };
        }

        /// <summary>
        /// Stops the main execution loop for this <see cref="FneSystemBase"/>.
        /// </summary>
        public override void Stop()
        {
            base.Stop();
        }

        /// <summary>
        /// Callback used to process whether or not a peer is being ignored for traffic.
        /// </summary>
        /// <param name="peerId">Peer ID</param>
        /// <param name="srcId">Source Address</param>
        /// <param name="dstId">Destination Address</param>
        /// <param name="slot">Slot Number</param>
        /// <param name="callType">Call Type (Group or Private)</param>
        /// <param name="frameType">Frame Type</param>
        /// <param name="dataType">DMR Data Type</param>
        /// <param name="streamId">Stream ID</param>
        /// <returns>True, if peer is ignored, otherwise false.</returns>
        protected override bool PeerIgnored(uint peerId, uint srcId, uint dstId, byte slot, fnecore.CallType callType, FrameType frameType, DMRDataType dataType, uint streamId)
        {
            return false;
        }

        /// <summary>
        /// Event handler used to handle a peer connected event.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected override void PeerConnected(object sender, PeerConnectedEvent e)
        {
            return;
        }

        /// <summary>
        /// KMM Key response handler from FNE
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        /// <exception cref="NotImplementedException"></exception>
        protected override void KeyResponse(object sender, KeyResponseEvent e)
        {
            byte[] payload = e.Data.Skip(11).ToArray();
            if (e.MessageId == (byte)KmmMessageType.MODIFY_KEY_CMD)
                foreach (VirtualChannel channel in RC2DVM.VirtualChannels)
                {
                    channel.KeyResponseReceived(e);
                }
        }

        /// <summary>
        /// Returns a new stream ID
        /// </summary>
        /// <returns></returns>
        public uint NewStreamId()
        {
            return (uint)rand.Next(int.MinValue, int.MaxValue);
        }

        /// <summary>
        /// Returns true if a tgid/timeslot combination is present in the list of active talkgroups
        /// </summary>
        /// <param name="tgid"></param>
        /// <param name="slot"></param>
        /// <returns></returns>
        public bool IsTalkgroupActive(uint tgid, byte slot = 1)
        {
            int index = activeTalkgroups.FindIndex(tg => tg.Item1 == tgid && tg.Item2 == slot);
            return index != -1;
        }

        /// <summary>
        /// Add a TGID/Slot pair to the list of active talkgroups
        /// </summary>
        /// <param name="tgid"></param>
        /// <param name="slot"></param>
        /// <returns></returns>
        public bool AddActiveTalkgroup(uint tgid, byte slot = 1)
        {
            if (!IsTalkgroupActive(tgid, slot))
            {
                activeTalkgroups.Add((tgid, slot));
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Remove a TGID/Slot pair from the list of active talkgroups
        /// </summary>
        /// <param name="tgid"></param>
        /// <param name="slot"></param>
        /// <returns></returns>
        public bool RemoveActiveTalkgroup(uint tgid, byte slot = 1)
        {
            if (IsTalkgroupActive(tgid, slot))
            {
                return activeTalkgroups.Remove((tgid, slot));
            }
            else
            {
                Log.Logger.Debug("Tried to remove TG/Slot not present in active talkgroup list!");
                return false;
            }
        }

    } // public abstract partial class FneSystemBase : fnecore.FneSystemBase
}
