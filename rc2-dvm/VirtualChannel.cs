using fnecore;
using fnecore.DMR;
using fnecore.P25;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Media;
using System.Reflection;
using rc2_core;
using NWaves.Filters.OnePole;
using NWaves.Filters.Butterworth;
using NWaves.Signals;

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

#if WIN32
        private AmbeVocoder extFullRateVocoder;
        private AmbeVocoder extHalfRateVocoder;
#endif

        private WaveFormat waveFormat = new WaveFormat(FneSystemBase.SAMPLE_RATE, FneSystemBase.BITS_PER_SECOND, 1);

        private bool callInProgress = false;

        private SlotStatus[] status;

        private uint txStreamId;

        private DVMRadio dvmRadio;

        // Local Audio Sidetone
        private WaveOutEvent waveOut;
        private BufferedWaveProvider bufferedWaveProvider;

        // Filter for audio
        private NWaves.Filters.Butterworth.BandPassFilter audioFilter;

        // MBE Tone Detector
        private MBEToneDetector toneDetector;

        // Whether the channel is "scanning" (able to receive from any talkgroup)
        private bool scanning = false;
        public bool Scanning { get { return scanning; } }

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

            // Whether to use external AMBE library
            bool externalAmbe = false;

#if WIN32
            // Try to find external AMBE.DLL interop library
            
            string codeBase = Assembly.GetExecutingAssembly().Location;
            UriBuilder uri = new UriBuilder(codeBase);
            string path = Uri.UnescapeDataString(uri.Path);
            string ambePath = Path.Combine(new string[] { Path.GetDirectoryName(path), "AMBE.DLL" });

            Log.Logger.Debug($"({Config.Name}) checking for external vocoder library at {ambePath}");

            if (File.Exists(ambePath))
            { 
                externalAmbe = true;
            }
#endif

            if (externalAmbe)
            {
                Log.Logger.Information($"({Config.Name}) AMBE.DLL found, using external vocoder interop!");
            }
            else
            {
                Log.Logger.Information($"({Config.Name}) Using software MBE vocoder");
            }

            // Instantiate the encoder/decoder pair based on the channel mode
            if (Config.Mode == VocoderMode.P25)
            {
                Log.Logger.Debug("Creating new P25 decoder/encoder");
                if (externalAmbe)
                {
#if WIN32
                    extFullRateVocoder = new AmbeVocoder();
#endif
                }
                else
                {
                    encoder = new MBEEncoder(MBE_MODE.IMBE_88BIT);
                    decoder = new MBEDecoder(MBE_MODE.IMBE_88BIT);
                }
            }
            else
            {
                Log.Logger.Debug("Creating new DMR decoder/encoder");
                if (externalAmbe)
                {
#if WIN32
                    extHalfRateVocoder = new AmbeVocoder(false);
#endif
                }
                else
                {
                    encoder = new MBEEncoder(MBE_MODE.DMR_AMBE);
                    decoder = new MBEDecoder(MBE_MODE.DMR_AMBE);
                }
                
            }

            // Init filter for audio
            float high_cutoff = (float)Config.AudioConfig.AudioHighCut / (float)waveFormat.SampleRate;
            float low_cutoff = (float)Config.AudioConfig.AudioLowCut / (float)waveFormat.SampleRate;
            audioFilter = new BandPassFilter(low_cutoff, high_cutoff, 8);

            // Tone detector
            toneDetector = new MBEToneDetector();

            // TX Local Repeat Audio
            if (Config.AudioConfig.TxLocalRepeat)
            {
                waveOut = new WaveOutEvent();
                waveOut.DeviceNumber = 0;
                bufferedWaveProvider = new BufferedWaveProvider(waveFormat) { DiscardOnBufferOverflow = true };
                waveOut.Init(bufferedWaveProvider);
                waveOut.Play();
            }

            // initialize slot statuses
            status = new SlotStatus[3];
            status[0] = new SlotStatus();  // DMR Slot 1
            status[1] = new SlotStatus();  // DMR Slot 2
            status[2] = new SlotStatus();  // P25

            Log.Logger.Information($"Configured virtual channel {Config.Name}");
            Log.Logger.Information($"    Mode: {Enum.GetName(typeof(VocoderMode), Config.Mode)}");
            Log.Logger.Information($"    Source ID: {Config.SourceId}");
            Log.Logger.Information($"    Listening on: {Config.ListenAddress}:{Config.ListenPort}");
            Log.Logger.Information($"    Audio Config:");
            Log.Logger.Information($"        Audio Bandpass:  {Config.AudioConfig.AudioLowCut} to {Config.AudioConfig.AudioHighCut} Hz");
            Log.Logger.Information($"        RX Audio Gain:   {Config.AudioConfig.RxAudioGain}");
            Log.Logger.Information($"        RX Vocoder Gain: {Config.AudioConfig.RxVocoderGain}");
            Log.Logger.Information($"        RX Vocoder AGC:  {Config.AudioConfig.RxVocoderAGC}");
            Log.Logger.Information($"        TX Audio Gain:   {Config.AudioConfig.TxAudioGain}");
            Log.Logger.Information($"        TX Vocoder Gain: {Config.AudioConfig.TxVocoderGain}");
            Log.Logger.Information($"        TX Local Repeat: {Config.AudioConfig.TxLocalRepeat}");
            Log.Logger.Information($"    Mode: {Config.Mode.ToString()}");
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
                Config.Talkgroups, this,
                HandleTxAudio,
                waveFormat.SampleRate
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
            // Stop transmit
            if (dvmRadio.Status.State == RadioState.Transmitting)
            {
                StopTransmit();
            }
            // End Stop
            if (currentTgIdx >= Config.Talkgroups.Count - 1)
            {
                return false;
            }
            else
            {
                // Increment channel
                currentTgIdx++;
                Log.Logger.Debug($"({Config.Name}) Selected TG {CurrentTalkgroup.Name} ({CurrentTalkgroup.DestinationId})");
                // Update Status
                dvmRadio.Status.ChannelName = CurrentTalkgroup.Name;
                // Reset any active calls
                resetCall();
                // Send group affiliation
                RC2DVM.fneSystem.peer.SendMasterGroupAffiliation(Config.SourceId, CurrentTalkgroup.DestinationId);
                // Send status update
                dvmRadio.StatusCallback();
                // Return success
                return true;
            }
        }

        /// <summary>
        /// Decrement the currently selected talkgroup index
        /// </summary>
        /// <returns>true if successful, else false</returns>
        public bool ChannelDown()
        {
            // Stop transmit
            if (dvmRadio.Status.State == RadioState.Transmitting)
            {
                StopTransmit();
            }
            // End Stop
            if (currentTgIdx > 0)
            {
                // Decrement channel
                currentTgIdx--;
                Log.Logger.Debug($"({Config.Name}) Selected TG {CurrentTalkgroup.Name} ({CurrentTalkgroup.DestinationId})");
                // Update Status
                dvmRadio.Status.ChannelName = CurrentTalkgroup.Name;
                // Reset any active calls
                resetCall();
                // Send group affiliation
                RC2DVM.fneSystem.peer.SendMasterGroupAffiliation(Config.SourceId, CurrentTalkgroup.DestinationId);
                // Send status update
                dvmRadio.StatusCallback();
                // Return success
                return true;
            }
            else { return false; }
        }

        private void resetCall()
        {
            dvmRadio.Status.State = RadioState.Idle;
            ignoreCall = false;
            callAlgoId = P25Defines.P25_ALGO_UNENCRYPT;
            callInProgress = false;
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

        public bool HasTalkgroupConfigured(VocoderMode mode, uint tgid, uint slot = 1)
        {
            if (mode != Config.Mode) { return false; }

            if (Config.Mode == VocoderMode.DMR)
            {
                int idx = Config.Talkgroups.FindIndex(tg => tg.DestinationId == tgid && tg.Timeslot == slot);
                return idx >= 0;
            }
            else
            {
                int idx = Config.Talkgroups.FindIndex(tg => tg.DestinationId == tgid);
                return idx >= 0;
            }
        }

        /// <summary>
        /// Initiate transmit to the system
        /// </summary>
        /// <returns>True if granted, false if not</returns>
        public bool StartTransmit()
        {
            // Check if talkgroup is active and return false if true
            if (RC2DVM.fneSystem.IsTalkgroupActive(CurrentTalkgroup.DestinationId, CurrentTalkgroup.Timeslot))
            {
                Log.Logger.Debug($"({Config.Name}) Cannot transmit on TG {CurrentTalkgroup.Name} ({CurrentTalkgroup.DestinationId}), call in progress");
                return false;
            }
            // "Grab" the talkgroup
            if (RC2DVM.fneSystem.AddActiveTalkgroup(CurrentTalkgroup.DestinationId, CurrentTalkgroup.Timeslot))
            {
                // Get new stream ID
                txStreamId = RC2DVM.fneSystem.NewStreamId();
                // Send Grant Demand if enabled
                if (Config.TxGrantDemands)
                {
                    RC2DVM.fneSystem.SendP25TDU(Config.SourceId, CurrentTalkgroup.DestinationId, true);
                }
                // Update status to transmitting
                dvmRadio.Status.State = RadioState.Transmitting;
                dvmRadio.StatusCallback();
                // Log
                Log.Logger.Information($"({Config.Name}) Start TX on TG {CurrentTalkgroup.Name} ({CurrentTalkgroup.DestinationId})");
                return true;
            } else
            {
                return false;
            }
        }

        /// <summary>
        /// Stop transmitting to the system
        /// </summary>
        /// <returns></returns>
        public bool StopTransmit()
        {
            Log.Logger.Information($"({Config.Name}) Stop TX on TG {CurrentTalkgroup.Name} ({CurrentTalkgroup.DestinationId})");
            // Send TDU
            RC2DVM.fneSystem.SendP25TDU(Config.SourceId, CurrentTalkgroup.DestinationId);
            // Update radio status
            dvmRadio.Status.State = RadioState.Idle;
            dvmRadio.StatusCallback();
            // Remove active TG
            return RC2DVM.fneSystem.RemoveActiveTalkgroup(CurrentTalkgroup.DestinationId, CurrentTalkgroup.Timeslot);
        }

        /// <summary>
        /// Handler for TX audio samples coming from WebRTC connection
        /// </summary>
        /// <param name="pcm16Samples"></param>
        /// <param name="pcmSampleRate"></param>
        public void HandleTxAudio(short[] pcm16Samples)
        {
            // Ignore if we're not transmitting
            if (dvmRadio.Status.State != RadioState.Transmitting)
            {
                return;
            }
            else
            {
                // Debug: play samples
                //byte[] pcm = new byte[pcm16Samples.Length * 2];
                //Buffer.BlockCopy(pcm16Samples, 0, pcm, 0, pcm.Length);
                //bufferedWaveProvider.AddSamples(pcm, 0, pcm.Length);

                // Send to the appropriate encoder
                if (Config.Mode == VocoderMode.P25)
                {
                    // Split up into 
                    P25EncodeAudioFrame(pcm16Samples, Config.SourceId, CurrentTalkgroup.DestinationId);
                }
                else if (Config.Mode == VocoderMode.DMR)
                {
                    // TODO: rework this function
                    //DMREncodeAudioFrame(Config.SourceId, CurrentTalkgroup.DestinationId, (byte)CurrentTalkgroup.Timeslot, pcm16Samples);
                }
            }
        }
    }
}
