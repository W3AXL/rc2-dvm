using fnecore;
using fnecore.DMR;
using NAudio.Wave;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using rc2_core;

namespace rc2_dvm
{
    /// <summary>
    /// Represents a single virtual channel which can be connected to RC2
    /// </summary>
    public partial class VirtualChannel
    {
        /// <summary>
        /// Configuration for this virtual channel
        /// </summary>
        public VirtualChannelConfigObject Config;

        private MBEEncoder encoder;
        private MBEDecoder decoder;

        private WaveFormat waveFormat = new WaveFormat(FneSystemBase.SAMPLE_RATE, FneSystemBase.BITS_PER_SECOND, 1);

        private bool callInProgress = false;

        private SlotStatus[] status;

        private uint txStreamId;

        private DVMRadio dvmRadio;

        /// <summary>
        /// Index of the currently selected talkgroup for this channel
        /// </summary>
        private int currentTgIdx = 0;

        /// <summary>
        /// Currently selected talkgroup for this channel
        /// </summary>
        public TalkgroupConfigObject CurrentTalkgroup
        {
            get
            {
                return Config.Talkgroups[currentTgIdx];
            }
        }

        /// <summary>
        /// Creates a new instance of a virtual channel
        /// </summary>
        /// <param name="config"></param>
        public VirtualChannel(VirtualChannelConfigObject config)
        {
            // Store channel configuration
            Config = config;

            // Instantiate the encoder/decoder pair based on the channel mode
            if (Config.Mode == VocoderMode.P25)
            {
                encoder = new MBEEncoder(MBEEncoder.MBE_ENCODER_MODE.ENCODE_88BIT_IMBE);
                decoder = new MBEDecoder(MBEDecoder.MBE_DECODER_MODE.DECODE_88BIT_IMBE);
            }
            else
            {
                encoder = new MBEEncoder(MBEEncoder.MBE_ENCODER_MODE.ENCODE_DMR_AMBE);
                decoder = new MBEDecoder(MBEDecoder.MBE_DECODER_MODE.DECODE_DMR_AMBE);
            }

            // initialize slot statuses
            status = new SlotStatus[3];
            status[0] = new SlotStatus();  // DMR Slot 1
            status[1] = new SlotStatus();  // DMR Slot 2
            status[2] = new SlotStatus();  // P25

            Log.Logger.Information($"Configured virtual channel {Config.Name}");
            Log.Logger.Information($"    Mode: {Config.Mode.ToString()}, Source ID: {Config.SourceId}, Listening on {Config.ListenAddress}:{Config.ListenPort}");
            Log.Logger.Information($"    Talkgroups ({Config.Talkgroups.Count}):");
            foreach (TalkgroupConfigObject talkgroup in Config.Talkgroups)
            {
                if (Config.Mode == VocoderMode.DMR)
                {
                    Log.Logger.Information($"        TGID {talkgroup.DestinationId} TS {talkgroup.Timeslot} ({talkgroup.Name})");
                }
                else
                {
                    Log.Logger.Information($"        TGID {talkgroup.DestinationId} ({talkgroup.Name})");
                }    
            }

            // Initialize new DVM radio
            dvmRadio = new DVMRadio(
                Config.Name, Config.RxOnly,
                Config.ListenAddress, Config.ListenPort,
                Config.Talkgroups, this
            );

            dvmRadio.Status.ZoneName = Config.Zone;
            dvmRadio.Status.ChannelName = CurrentTalkgroup.Name;

        }

        /// <summary>
        /// Start the virtual channel
        /// </summary>
        public void Start()
        {
            // Start radio
            dvmRadio.Start();
        }

        /// <summary>
        /// Stop the virtual channel
        /// </summary>
        public void Stop()
        {
            // Stop radio
            dvmRadio.Stop();
        }

        /// <summary>
        /// Increment the currently selected talkgroup index
        /// </summary>
        /// <returns>true if successful, else false</returns>
        public bool ChannelUp()
        {
            if (currentTgIdx >= Config.Talkgroups.Count - 1)
            {
                return false;
            }
            else
            {
                currentTgIdx++;
                Log.Logger.Debug($"({Config.Name}) Selected TG {CurrentTalkgroup.Name}");
                dvmRadio.Status.ChannelName = CurrentTalkgroup.Name;
                dvmRadio.Status.State = RadioState.Idle;
                dvmRadio.StatusCallback();
                return true;
            }
        }

        /// <summary>
        /// Decrement the currently selected talkgroup index
        /// </summary>
        /// <returns>true if successful, else false</returns>
        public bool ChannelDown()
        {
            if (currentTgIdx > 0)
            {
                currentTgIdx--;
                Log.Logger.Debug($"({Config.Name}) Selected TG {CurrentTalkgroup.Name}");
                dvmRadio.Status.ChannelName = CurrentTalkgroup.Name;
                dvmRadio.Status.State = RadioState.Idle;
                dvmRadio.StatusCallback();
                return true;
            }
            else { return false; }
        }

        private void UpdateStatus()
        {

        }

        /// <summary>
        /// Return whether or not the virtual channel is configured for the specified talkgroup/timeslot
        /// </summary>
        /// <param name="tgid"></param>
        /// <param name="slot"></param>
        /// <returns></returns>
        public bool IsTalkgroupSelected(VocoderMode mode, uint tgid, uint slot = 1)
        {
            if (Config.Mode == VocoderMode.DMR)
            {
                if (mode == VocoderMode.P25) { return false; }
                if (tgid == CurrentTalkgroup.DestinationId && slot == CurrentTalkgroup.Timeslot) { return true; }
            }
            else
            {
                if (mode == VocoderMode.DMR) { return false; }
                if (tgid == CurrentTalkgroup.DestinationId) {  return true; } 
            }
            return false;
        }
    }
}
