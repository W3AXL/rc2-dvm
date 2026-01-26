using fnecore;
using fnecore.DMR;
using fnecore.P25;
using fnecore.P25.KMM;
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
using NWaves.Filters.Butterworth;
using System.Timers;
using TinyJson;
using NAudio.Dsp;
using NWaves.Signals;
using NWaves.Signals.Builders;
using NWaves.Signals.Builders.Base;
using NWaves.Audio;
using NWaves.Operations;
using Org.BouncyCastle.Asn1;
using NAudio.Midi;

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

        private NAudio.Wave.WaveFormat waveFormat = new NAudio.Wave.WaveFormat(FneSystemBase.SAMPLE_RATE, FneSystemBase.BITS_PER_SECOND, 1);

        private bool callInProgress = false;

        private SlotStatus[] status;

        private uint txStreamId;

        // Timer to clear RX state on lack of LDUs
        private System.Timers.Timer rxDataTimer;

        // Variables for displaying active source ID
        private bool showingSourceId;
        private uint lastSourceId;
        private System.Timers.Timer sourceIdTimer;

        private DVMRadio dvmRadio;

        // Local Audio Sidetone
        private WaveOutEvent waveOut;
        private BufferedWaveProvider bufferedWaveProvider;

        // Filters for TX/RX audio 
        private LowPassFilter txAudioFilter;
        private LowPassFilter rxAudioFilter;

        // MBE Tone Detector
        private MBEToneDetector toneDetector;

        // 9 MBE frames per LDU
        const int LDU_SAMPLES_LENGTH = FneSystemBase.MBE_SAMPLES_LENGTH * 9;

        // Sounds
        private short[] toneAtg;

        // Whether the channel is "scanning" (able to receive from any talkgroup)
        public bool Scanning { get; private set; }
        // The channel index we've landed on during scan (null is default state)
        private TalkgroupConfigObject? scanLandedTg = null;
        // Timer for waiting on a channel after it's landed before returning to the selected channel
        private System.Timers.Timer scanHangTimer;

        /// <summary>
        /// State variable to keep track of whether secure is turned on or off
        /// </summary>
        public bool Secure = false;

        /// <summary>
        /// Index of the currently selected talkgroup for this channel
        /// </summary>
        private int currentTgIdx = 0;

        /// <summary>
        /// Timer to trigger an affiliation after a delay
        /// </summary>
        private System.Timers.Timer affHoldoffTimer;

        /// <summary>
        /// Key container opened by program
        /// </summary>
        private KeyContainer keyContainer;

        /// <summary>
        /// Currently loaded encryption keys
        /// </summary>
        private Dictionary<ushort, KeyItem> loadedKeys = [];

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
        /// The talkgroup currently being transmitted on
        /// </summary>
        private TalkgroupConfigObject? txTalkgroup = null;

        /// <summary>
        /// The index of the home talkgroup in the talkgroup list
        /// </summary>
        private int homeTalkgroupIndex = -1;

        /// <summary>
        /// Creates a new instance of a virtual channel
        /// </summary>
        /// <param name="config"></param>
        public VirtualChannel(VirtualChannelConfigObject config, KeyContainer keyContainer)
        {
            // Store channel configuration
            Config = config;

            // Whether to use external AMBE library
            bool externalAmbe = false;

            // Store encryption settings
            this.keyContainer = keyContainer;

            // Fallback to global audio config if none provided
            if (Config.AudioConfig == null)
            {
                Config.AudioConfig = RC2DVM.Configuration.AudioConfig;
                Log.Logger.Information("({0:l}) using global audio config", Config.Name);
            }

            // Fallback to global talkgroup list if none provided
            if (Config.Talkgroups == null)
            {
                // Ensure we have a global list
                if (RC2DVM.Configuration.Talkgroups == null)
                    throw new Exception($"Virtual channel {Config.Name} has no talkgroups list and no global talkgroup list defined!");
                // Load it
                Config.Talkgroups = RC2DVM.Configuration.Talkgroups;
                Log.Logger.Information("({0:l}) using global talkgroups list", Config.Name);
            }

            // Fallback to global scan config if none provided
            if (Config.ScanConfig == null)
            {
                // Ensure we have a global list
                if (RC2DVM.Configuration.ScanConfig == null)
                    throw new Exception($"Virtual channel {Config.Name} has no scan configuration and no global scan configuration defined!");
                // Load it
                Config.ScanConfig = RC2DVM.Configuration.ScanConfig;
            }

            // Load home talkgroup
            homeTalkgroupIndex = Config.Talkgroups.FindIndex(tg => tg.Name == Config.HomeTalkgroup);
            if (homeTalkgroupIndex < 0)
                Log.Logger.Warning("Could not find home talkgroup matching '{0}'", Config.HomeTalkgroup);
            else
                currentTgIdx = homeTalkgroupIndex;

                // Load sounds
                LoadSounds();

#if WIN32
            // Try to find external AMBE.DLL interop library
            string codeBase = System.AppContext.BaseDirectory;
            UriBuilder uri = new UriBuilder(codeBase);
            string path = Uri.UnescapeDataString(uri.Path);
            string ambePath = Path.Combine(new string[] { Path.GetDirectoryName(path), "AMBE.DLL" });

            Log.Logger.Debug("({0:l}) checking for external vocoder library...", Config.Name);

            if (File.Exists(ambePath))
            { 
                externalAmbe = true;
            }
#endif

            if (externalAmbe)
            {
                Log.Logger.Information("({0:l}) AMBE.DLL found, using external vocoder interop!", Config.Name);
            }
            else
            {
                Log.Logger.Information("({0:l}) Using software MBE vocoder", Config.Name);
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

            // Init source ID display stuff
            sourceIdTimer = new System.Timers.Timer(1000);
            sourceIdTimer.Elapsed += sourceIdTimerCallback;
            //sourceIdTimer.Enabled = true;

            // Init rx data timeout timer
            rxDataTimer = new System.Timers.Timer(1000);
            rxDataTimer.Elapsed += rxDataTimeout;

            // Init filters for TX & RX audio
            float high_cutoff = (float)Config.AudioConfig.AudioHighCut / (float)waveFormat.SampleRate;
            txAudioFilter = new LowPassFilter(high_cutoff, 8);
            rxAudioFilter = new LowPassFilter(high_cutoff, 8);

            // Tone detector
            toneDetector = new MBEToneDetector(Config.AudioConfig.TxToneRatio, Config.AudioConfig.TxToneHits, Config.AudioConfig.TxToneLowerLimit, Config.AudioConfig.TxToneUpperLimit);

            // TX Local Repeat Audio
            if (Config.AudioConfig.TxLocalRepeat)
            {
                waveOut = new WaveOutEvent();
                waveOut.DeviceNumber = 0;
                bufferedWaveProvider = new BufferedWaveProvider(waveFormat) { DiscardOnBufferOverflow = true };
                waveOut.Init(bufferedWaveProvider);
                waveOut.Play();
            }

            // Initialize affiliation holdoff timer
            affHoldoffTimer = new System.Timers.Timer(1000);
            affHoldoffTimer.Elapsed += affToCurrentChannel;
            affHoldoffTimer.AutoReset = false;
            if (RC2DVM.Configuration.Network.SendChannelAffiliations)
            {
                affHoldoffTimer.Enabled = true;
            }
            else
            {
                affHoldoffTimer.Enabled = false;
            }

            // Initialize scan hang timer
            scanHangTimer = new System.Timers.Timer(Config.ScanConfig.Hangtime);
            scanHangTimer.Elapsed += scanHangTimerCallback;
            scanHangTimer.Enabled = false;
            scanHangTimer.AutoReset = false;
                
            // initialize slot statuses
            status = new SlotStatus[3];
            status[0] = new SlotStatus();  // DMR Slot 1
            status[1] = new SlotStatus();  // DMR Slot 2
            status[2] = new SlotStatus();  // P25
            
            // Configuration log prints
            Log.Logger.Information("Configured virtual channel {name:l}", Config.Name);
            Log.Logger.Information("    Source ID: {SourceId}", Config.SourceId);
            Log.Logger.Information("    Listening on: {ListenAddress:l}:{ListenPort}", Config.ListenAddress, Config.ListenPort);
            Log.Logger.Information("    Audio Config:");
            Log.Logger.Information("        Audio Lowpass:       {AudioHighCut} Hz", Config.AudioConfig.AudioHighCut);
            Log.Logger.Information("        RX Audio Gain:       {RxAudioGain}", Config.AudioConfig.RxAudioGain);
            Log.Logger.Information("        RX Vocoder Gain:     {RxVocoderGain}", Config.AudioConfig.RxVocoderGain);
            Log.Logger.Information("        RX Vocoder AGC:      {RxVocoderAGC}", Config.AudioConfig.RxVocoderAGC);
            Log.Logger.Information("        TX Audio Gain:       {TxAudioGain}", Config.AudioConfig.TxAudioGain);
            Log.Logger.Information("        TX Vocoder Gain:     {TxVocoderGain}", Config.AudioConfig.TxVocoderGain);
            Log.Logger.Information("        TX Local Repeat:     {TxLocalRepeat}", Config.AudioConfig.TxLocalRepeat);
            Log.Logger.Information("        TX Tone Detection:   {TxToneDetection}", Config.AudioConfig.TxToneDetection);
            if (Config.AudioConfig.TxToneDetection)
            {
                Log.Logger.Information("        TX Tone Threshold:   {Config.AudioConfig.TxToneRatio}", Config.AudioConfig.TxToneRatio);
                Log.Logger.Information("        TX Tone Hits:        {Config.AudioConfig.TxToneHits}", Config.AudioConfig.TxToneHits);
                Log.Logger.Information("        TX Tone Valid Range: {TxToneLowerLimit} Hz to {TxToneUpperLimit} Hz", Config.AudioConfig.TxToneLowerLimit, Config.AudioConfig.TxToneUpperLimit);
            }
            Log.Logger.Information("    Mode: {Mode:l}", Config.Mode.ToString());
            // Print ATG info
            if (Config.AnnouncementGroup > 0)
            {
                Log.Logger.Information("    Announcement Group: {ATG:l}", Config.AnnouncementGroup.ToString());
            }
            // Print configured talkgroups
            Log.Logger.Information("    Talkgroups ({TGCount}):", Config.Talkgroups.Count);
            foreach (TalkgroupConfigObject talkgroup in Config.Talkgroups)
            {
                string encStr = "";
                if (talkgroup.AlgId != P25Defines.P25_ALGO_UNENCRYPT && talkgroup.KeyId != 0)
                    encStr = $", {Enum.GetName(typeof(Algorithm), talkgroup.AlgId)} Key ID {talkgroup.KeyId}, {(talkgroup.Strapped ? "STRAPPED" : "SELECTABLE")}";
                if (Config.Mode == VocoderMode.DMR)
                {
                    Log.Logger.Information("      {Scan:l} TGID {DestinationId} TS {Timeslot} ({Name:l}){Enc:l}", talkgroup.Scan ? "S" : " ", talkgroup.DestinationId, talkgroup.Timeslot, talkgroup.Name, encStr);
                }
                else
                {
                    Log.Logger.Information("      {Scan:l} TGID {DestinationId} ({Name:l}){Enc:l}", talkgroup.Scan ? "S" : " ", talkgroup.DestinationId, talkgroup.Name, encStr);
                }    
            }
            // Print Home Talkgroup
            Log.Logger.Information("    Home Talkgroup: {0}", homeTalkgroupIndex >= 0 ? Config.HomeTalkgroup : "None");
            // Print Scan Configuration
            Log.Logger.Information("    Scan Configuration:");
            Log.Logger.Information("        Talkback: {talkback:l}", Config.ScanConfig.Talkback ? "Enabled" : "Disabled");
            Log.Logger.Information("        Hangtime: {hangtime} ms", Config.ScanConfig.Hangtime);

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
        /// Callback to alternate between TG name and source ID
        /// </summary>
        /// <param name="source"></param>
        /// <param name="e"></param>
        private void sourceIdTimerCallback(Object source, ElapsedEventArgs e)
        {
            if (showingSourceId)
            {
                // We show either the selected TG name or the scan landed name
                if (scanLandedTg != null)
                    dvmRadio.Status.ChannelName = scanLandedTg.Name;
                else
                    dvmRadio.Status.ChannelName = CurrentTalkgroup.Name;
                // Update status
                dvmRadio.StatusCallback();
                showingSourceId = false;
            }
            else
            {
                dvmRadio.Status.ChannelName = $"ID: {lastSourceId}";
                dvmRadio.StatusCallback();
                showingSourceId = true;
            }
        }

        /// <summary>
        /// Function called when the rx data timeout timer is hit, will force-reset the call data on loss of LDUs
        /// </summary>
        /// <param name="source"></param>
        /// <param name="e"></param>
        private void rxDataTimeout(Object source, ElapsedEventArgs e)
        {
            Log.Logger.Warning("({0:l}) RX data timeout, resetting call", Config.Name);
            resetCall();
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
                // Update Status
                dvmRadio.Status.ChannelName = CurrentTalkgroup.Name;
                // Reset any active calls
                resetCall();
                // Restart affiliation timer
                resetAffTimer();
                // Send status update
                dvmRadio.StatusCallback();
                // Log
                Log.Logger.Debug("({0:l}) Selected TG {1:l} ({2})", Config.Name, CurrentTalkgroup.Name, CurrentTalkgroup.DestinationId);
                // Return channel setup success
                return SetupChannel();
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
                // Update Status
                dvmRadio.Status.ChannelName = CurrentTalkgroup.Name;
                // Reset any active calls
                resetCall();
                // Restart affiliation timer
                resetAffTimer();
                // Log
                Log.Logger.Debug("({0:l}) Selected TG {1:l} ({2})", Config.Name, CurrentTalkgroup.Name, CurrentTalkgroup.DestinationId);
                // Return channel setup success
                return SetupChannel();
            }
            else { return false; }
        }

        /// <summary>
        /// Goto the specified channel index
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public bool ChannelIndex(int index)
        {
            // Stop transmit
            if (dvmRadio.Status.State == RadioState.Transmitting)
                StopTransmit();

            // Ensure index is in range
            if (index < 0 || index >= Config.Talkgroups.Count)
            {
                Log.Logger.Error("({0:l}) Cannot goto talkgroup index {0}, out of range!", index);
                return false;
            }

            // Goto Channel
            currentTgIdx = index;
            // Update Status
            dvmRadio.Status.ChannelName = CurrentTalkgroup.Name;
            // Reset any call
            resetCall();
            // Restart aff timer
            resetAffTimer();
            // Log
            Log.Logger.Debug("({0:l}) Selected TG {1:l} ({2})", Config.Name, CurrentTalkgroup.Name, CurrentTalkgroup.DestinationId);
            // Return setup success
            return SetupChannel();
        }

        /// <summary>
        /// Callback when a new channel is selected (handles configuration of encryption, etc)
        /// </summary>
        public bool SetupChannel()
        {
            // Setup encryption if configured
            if (CurrentTalkgroup.AlgId != P25Defines.P25_ALGO_UNENCRYPT)
            {
                // Ensure key ID is set
                if (CurrentTalkgroup.KeyId == 0)
                {
                    Log.Logger.Error("({0:l}) KEYFAIL: {TG} ({TGID}) is configured for encryption but has Key ID 0", Config.Name, CurrentTalkgroup.Name, CurrentTalkgroup.DestinationId);
                    return false;
                }
                // Load the key if it's not loaded already
                if (!loadedKeys.ContainsKey(CurrentTalkgroup.KeyId))
                {
                    // Try to get the key from the local file
                    KeyItem key = keyContainer.GetKeyById(CurrentTalkgroup.KeyId);
                    if (key != null)
                    {
                        loadedKeys[key.KeyId] = key;
                        Log.Logger.Information("({0:l}) Loaded Key ID 0x{KeyId:X4} ({Algo:l}) from keyfile into local keystore", Config.Name, key.KeyId, Enum.GetName(typeof(Algorithm), key.KeyFormat));
                    }
                    // Request from FNE as a fallback
                    else
                    {
                        Log.Logger.Information("({0:l}) Key ID 0x{keyId:X4} not found in local keyfile, requesting from FNE", Config.Name, CurrentTalkgroup.KeyId);
                        RC2DVM.fneSystem.peer.SendMasterKeyRequest(CurrentTalkgroup.AlgId, CurrentTalkgroup.KeyId);
                    }
                }
            }

            // Update secure softkey & radio state
            if (CurrentTalkgroup.Strapped || Secure)
            {
                // Update status
                dvmRadio.Status.Secure = true;
                // Update softkey
                int keyIdx = dvmRadio.Status.Softkeys.FindIndex(key => key.Name == SoftkeyName.SEC);
                dvmRadio.Status.Softkeys[keyIdx].State = SoftkeyState.On;
            }
            else
            {
                // Update status
                dvmRadio.Status.Secure = false;
                // Update softkey
                int keyIdx = dvmRadio.Status.Softkeys.FindIndex(key => key.Name == SoftkeyName.SEC);
                dvmRadio.Status.Softkeys[keyIdx].State = SoftkeyState.Off;
            }

            // Send status update
            dvmRadio.StatusCallback();

            // Return true if nothing failed
            return true;
        }

        /// <summary>
        /// Handler for FNE key response
        /// </summary>
        /// <param name="e"></param>
        public void KeyResponseReceived(KeyResponseEvent e)
        {
            // Add any received keys to the local keystore, unless they already exist
            foreach(KeyItem key in e.KmmKey.KeysetItem.Keys)
            {
                if (!loadedKeys.ContainsKey(key.KeyId))
                {
                    loadedKeys[key.KeyId] = key;
                    Log.Logger.Information("({0:l}) Loaded Key ID 0x{KeyID:X4} ({Algo}) from FNE KMM into local keystore", Config.Name, key.KeyId, Enum.GetName(typeof(Algorithm), key.KeyFormat));
                }
            }
        }

        /// <summary>
        /// Callback for affiliation holdoff timer to send the group affiliation for the currently selected talkgroup
        /// </summary>
        /// <param name="source"></param>
        /// <param name="e"></param>
        private void affToCurrentChannel(Object source, ElapsedEventArgs e)
        {
            Log.Logger.Information("({0:l}) Sending GRP AFF for TG {tg} ({tgid})", Config.Name, CurrentTalkgroup.Name, CurrentTalkgroup.DestinationId);
            RC2DVM.fneSystem.peer.SendMasterGroupAffiliation(Config.SourceId, CurrentTalkgroup.DestinationId);
        }

        /// <summary>
        /// Resets and restarts the affiliation holdoff timer
        /// </summary>
        private void resetAffTimer()
        {
            if (RC2DVM.Configuration.Network.SendChannelAffiliations)
            {
                Log.Logger.Debug("({0:l}) Restarting affiliation holdoff timer", Config.Name);
                affHoldoffTimer.Stop();
                affHoldoffTimer.Start();
            }
        }

        /// <summary>
        /// Resets current call data
        /// </summary>
        private void resetCall()
        {
            // Stop source ID callback
            sourceIdTimer.Stop();
            // Stop rx data timeout timer
            rxDataTimer.Stop();
            // Reset P25 counter
            p25N = 0;
            // Update status
            dvmRadio.Status.State = RadioState.Idle;
            ignoreCall = false;
            callAlgoId = P25Defines.P25_ALGO_UNENCRYPT;
            FneUtils.Memset(callMi, 0x00, P25Defines.P25_MI_LENGTH);
            callInProgress = false;
            // Send status
            dvmRadio.StatusCallback();
        }

        /// <summary>
        /// Callback fired when the scan hang timer elapses
        /// </summary>
        private void scanHangTimerCallback(Object source, ElapsedEventArgs e)
        {
            // Debug
            Log.Logger.Debug("({0:l}) Scan hang timer expiration, reverting to seleted talkgroup", Config.Name);
            // Stop the hang timer
            scanHangTimer.Stop();
            // Reset the channel text & update status
            dvmRadio.Status.ChannelName = CurrentTalkgroup.Name;
            dvmRadio.StatusCallback();
            // Reset the landed tg
            scanLandedTg = null;
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

        /// <summary>
        /// Returns true if this virtual channel has the specified mode/talkgroup/timeslot configured in its list
        /// </summary>
        /// <param name="mode"></param>
        /// <param name="tgid"></param>
        /// <param name="slot"></param>
        /// <returns></returns>
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
        /// Returns whether the given talkgroup is in the channel's scanlist
        /// </summary>
        /// <param name="tgid"></param>
        /// <param name="slot"></param>
        public bool HasTgInScanlist(VocoderMode mode, uint tgid, uint slot = 1)
        {
            if (mode != Config.Mode) { return false; }

            if (Config.Mode == VocoderMode.DMR)
            {
                return Config.Talkgroups?.Any(tg => tg.DestinationId == tgid && tg.Timeslot == slot && tg.Scan == true) ?? false;
            }
            else
            {
                return Config.Talkgroups?.Any(tg => tg.DestinationId == tgid && tg.Scan == true) ?? false;
            }
        }

        /// <summary>
        /// Initiate transmit to the system
        /// </summary>
        /// <returns>True if granted, false if not</returns>
        public bool StartTransmit()
        {
            // By default, the talkgroup we're going to transmit on is the currently selected talkgroup
            txTalkgroup = CurrentTalkgroup;

            // This checks if we're scanning and landed on a channel
            if (Scanning && scanLandedTg != null)
            {
                // If talkback is enabled, we want to transmit on the landed channel
                if (Config.ScanConfig.Talkback)
                {
                    txTalkgroup = scanLandedTg;
                    Log.Logger.Debug("({0:l}) Scan talkback enabled, transmitting on landed talkgroup {tg} ({id})", Config.Name, txTalkgroup.Name, txTalkgroup.DestinationId);
                }
                // Otherwise we reset the landed channel to none & reset the channel name
                else
                {
                    scanLandedTg = null;
                    dvmRadio.Status.ChannelName = txTalkgroup.Name;
                    Log.Logger.Debug("({0:l}) Scan talkback disabled, reverting to selected talkgroup {tg} ({id)", Config.Name, txTalkgroup.Name, txTalkgroup.DestinationId);
                }
                // Either way, we stop the scan hang timer for the duration of the TX
                Log.Logger.Debug("({0:l}) TX starting, scan hang timer stopped", Config.Name);
                scanHangTimer.Stop();
            }

            // Check if talkgroup is active and return false if true
            if (RC2DVM.fneSystem.IsTalkgroupActive(txTalkgroup.DestinationId, txTalkgroup.Timeslot))
            {
                Log.Logger.Debug("({0:l}) Cannot transmit on TG {1} ({2}), call in progress", Config.Name, txTalkgroup.Name, txTalkgroup.DestinationId);
                return false;
            }
            // "Grab" the talkgroup
            if (RC2DVM.fneSystem.AddActiveTalkgroup(txTalkgroup.DestinationId, txTalkgroup.Timeslot))
            {
                // Setup Crypto
                if (txTalkgroup.KeyId != 0 && (Secure || txTalkgroup.Strapped))
                {
                    crypto.SetKey(txTalkgroup.KeyId, txTalkgroup.AlgId, loadedKeys[txTalkgroup.KeyId].GetKey());
                    Log.Logger.Information("({0:l}) Start ENC TX on TG {1} ({2})", Config.Name, txTalkgroup.Name, txTalkgroup.DestinationId);
                }
                else
                {
                    crypto.SetKey(txTalkgroup.KeyId, P25Defines.P25_ALGO_UNENCRYPT, new byte[7]);
                    Log.Logger.Information("({0:l}) Start TX on TG {1} ({2})", Config.Name, txTalkgroup.Name, txTalkgroup.DestinationId);
                }

                // Get new stream ID
                txStreamId = RC2DVM.fneSystem.NewStreamId();
                // Send Grant Demand if enabled
                if (Config.TxGrantDemands)
                {
                    // Send the grant demand
                    SendP25TDU(Config.SourceId, txTalkgroup.DestinationId, true, false);
                }
                // Update status to transmitting
                dvmRadio.Status.State = RadioState.Transmitting;
                dvmRadio.StatusCallback();

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
            // Do nothing if we're not transmitting
            if (dvmRadio.Status.State != RadioState.Transmitting)
            {
                return false;
            }
            // Catch a null tx talkgroup (shouldn't happen)
            if (txTalkgroup == null)
            {
                Log.Logger.Error("({0:l}) Cannot stop TX, txTalkgroup is null!");
                return false;
            }
            // Log
            Log.Logger.Information("({0:l}) Stop TX on TG {1} ({2})", Config.Name, txTalkgroup.Name, txTalkgroup.DestinationId);
            // Send TDU via helper (with call termination enabled)
            SendP25TDU(Config.SourceId, txTalkgroup.DestinationId, false, true);
            // Reset call
            resetCall();
            // Restart the scan hang timer if Scanning
            if (Scanning)
            {
                Log.Logger.Debug("({0:l}) Transmit done, restarting scan hang timer", Config.Name);
                scanHangTimer.Stop();
                scanHangTimer.Start();
            }
            // Update radio status
            dvmRadio.Status.State = RadioState.Idle;
            dvmRadio.StatusCallback();
            // Remove active TG
            return RC2DVM.fneSystem.RemoveActiveTalkgroup(txTalkgroup.DestinationId, txTalkgroup.Timeslot);
        }

        /// <summary>
        /// Returns whether the virtual channel is currently transmitting
        /// </summary>
        /// <returns></returns>
        public bool IsTransmitting()
        {
            if (dvmRadio.Status.State == RadioState.Transmitting) { return true; } else { return false; }
        }

        /// <summary>
        /// Returns whether the virtual channel is currently receiving
        /// </summary>
        /// <returns></returns>
        public bool IsReceiving()
        {
            if (dvmRadio.Status.State == RadioState.Receiving) { return true; } else { return false; }
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

        private void LoadSounds()
        {
            Log.Logger.Debug("Loading sounds for virtual channel");

            // Load ATG tone
            WaveFile atgTone = new WaveFile(rc2_dvm.Properties.Resources.sndAtgTone);
            DiscreteSignal atgSignal = atgTone.Signals[0] * 0.25f; // -6 dB

            // Resample if needed
            if (atgSignal.SamplingRate != waveFormat.SampleRate)
            {
                Resampler resampler = new Resampler();
                DiscreteSignal temp = resampler.Resample(atgSignal, waveFormat.SampleRate);
                atgSignal = temp;
            }

            // Add silence at the start to account for console unmute delay (200ms seems to be good)
            atgSignal = atgSignal.Delay(200 * (waveFormat.SampleRate / 1000));

            // Calculate padded length to neatly align with a 9-MBE frame LDU
            int paddedLength = (int)Math.Ceiling((double)atgSignal.Samples.Length / LDU_SAMPLES_LENGTH) * LDU_SAMPLES_LENGTH;

            // Pad the signal
            atgSignal = atgSignal.Delay(paddedLength - atgSignal.Samples.Length);
            //Log.Logger.Debug("Padded ATG tone to {0} samples", paddedLength);

            // Load the final samples into memory
            toneAtg = Utils.FloatToPcm(atgSignal.Samples);
        }

        /// <summary>
        /// Start the ATG tone playing and return the number of P25 MBE frames to skip
        /// </summary>
        /// <returns></returns>
        public int PlayAtgTone()
        {
            // Skip if not loaded
            if (toneAtg == null) { return 0; }
            // Calculate the number of MBE frames to skip
            int skipFrames = toneAtg.Length / LDU_SAMPLES_LENGTH;
            // Debug print
            Log.Logger.Debug("Sending ATG tone to Radio ({0} samples / {1} LDU frames)", toneAtg.Length, skipFrames);
            // Send audio
            dvmRadio.RxSendPCM16Samples(toneAtg, (uint)waveFormat.SampleRate);
            // Return
            return skipFrames;
        }

        /// <summary>
        /// Goto the configured home channel, or return false if home channel not configured
        /// </summary>
        /// <returns></returns>
        public bool GoHome()
        {
            // Return false if not configured
            if (homeTalkgroupIndex < 0) { return false; }
            // Goto channel if configured
            return ChannelIndex(homeTalkgroupIndex);
        }

        /// <summary>
        /// Toggle the scan state of the virtual channel
        /// </summary>
        public bool ToggleScan()
        {
            if (Scanning)
            {
                Scanning = false;
                Log.Logger.Debug("({0:l}) Scan disabled, stopping hang timer", Config.Name);
                scanHangTimer.Stop();
                scanLandedTg = null;
                // Update radio state
                dvmRadio.Status.ScanState = ScanState.NotScanning;
                // Update softkey
                int keyIdx = dvmRadio.Status.Softkeys.FindIndex(key => key.Name == SoftkeyName.SCAN);
                dvmRadio.Status.Softkeys[keyIdx].State = SoftkeyState.Off;
            }
            else
            {
                Scanning = true;
                Log.Logger.Debug("({0:l}) Scan enabled", Config.Name);
                // Update radio state
                dvmRadio.Status.ScanState = ScanState.Scanning;
                // Update softkey
                int keyIdx = dvmRadio.Status.Softkeys.FindIndex(key => key.Name == SoftkeyName.SCAN);
                dvmRadio.Status.Softkeys[keyIdx].State = SoftkeyState.On;
            }
            // Status update
            dvmRadio.StatusCallback();
            // Always return true for now (TODO: Return false for invalid scan configurations)
            return true;
        }
    }
}
