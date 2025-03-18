using System;
using System.Net;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using fnecore;

namespace rc2_dvm
{
    /// <summary>
    /// Valid modes for audio vocoding
    /// </summary>
    public enum VocoderMode
    {
        DMR = 1,
        P25 = 2,
    }
    
    /// <summary>
    /// Class which contains configuration for logging
    /// </summary>
    public class LogConfigObject
    {
        /// <summary>
        /// Logging Display Level
        /// </summary>
        public int DisplayLevel = 1;
        /// <summary>
        /// Logging File Level
        /// </summary>
        public int FileLevel = 1;
        /// <summary>
        /// Path for log files
        /// </summary>
        public string FilePath = ".";
        /// <summary>
        /// Path for activity log files
        /// </summary>
        public string ActivityFilePath = ".";
        /// <summary>
        /// Root name for the log files
        /// </summary>
        public string FileRoot = "dvm-rc2";
    }

    /// <summary>
    /// Class representing a vocoder audio configuration
    /// </summary>
    public class AudioConfigObject
    {
        public uint AudioLowCut = 200;

        public uint AudioHighCut = 3000;

        public float RxAudioGain = 1.0f;

        public float RxVocoderGain = 3.0f;

        public bool RxVocoderAGC = false;

        public float TxAudioGain = 1.0f;

        public float TxVocoderGain = 3.0f;

        /// <summary>
        /// Whether to play TX audio through the local default speakers
        /// </summary>
        public bool TxLocalRepeat = false;

        /// <summary>
        /// Whether to enable tone detection logic for the channel
        /// </summary>
        public bool TxToneDetection = false;
        /// <summary>
        /// Ratio of tone frequency to rest of FFT
        /// </summary>
        public int TxToneRatio = 90;
        /// <summary>
        /// Number of sequential tone detections required before a tone is transmitted
        /// </summary>
        public int TxToneHits = 3;
        /// <summary>
        /// Lower limit for tone detection in Hz
        /// </summary>
        public int TxToneLowerLimit = 280;
        /// <summary>
        /// Upper limit for tone detection in Hz
        /// </summary>
        public int TxToneUpperLimit = 3000;
    }

    /// <summary>
    /// Class representing a system talkgroup
    /// </summary>
    public class TalkgroupConfigObject
    {
        /// <summary>
        /// Destination TGID
        /// </summary>
        public uint DestinationId;
        /// <summary>
        /// Destination Timeslot
        /// </summary>
        public byte Timeslot = 1;
        /// <summary>
        /// Talkgroup Name
        /// </summary>
        public string Name;
    }

    /// <summary>
    /// Configuration for each virtual channel that connects to dvm-rc2
    /// </summary>
    public class VirtualChannelConfigObject
    {
        /// <summary>
        /// Name of this virtual channel
        /// </summary>
        public string Name = "VirtualChannel";
        /// <summary>
        /// Zone text for RC2
        /// </summary>
        public string Zone = "";
        /// <summary>
        /// Listen address for the console client to connect to
        /// </summary>
        public IPAddress ListenAddress = IPAddress.Parse("0.0.0.0");
        /// <summary>
        /// Listen port for the console client to connect to
        /// </summary>
        public int ListenPort = 8810;
        /// <summary>
        /// Whether this virtual channel is DMR or P25
        /// </summary>
        public VocoderMode Mode = VocoderMode.DMR;
        /// <summary>
        /// Audio configuration for this virtual channel's vocoder
        /// </summary>
        public AudioConfigObject AudioConfig = new AudioConfigObject();
        /// <summary>
        /// Source ID (aka Radio ID) for this virtual channel
        /// </summary>
        public uint SourceId;
        /// <summary>
        /// List of available talkgroups for this virtual channel
        /// </summary>
        public List<TalkgroupConfigObject> Talkgroups = new List<TalkgroupConfigObject>();
        /// <summary>
        /// Whether this channel is RX-only or not (TX disabled)
        /// </summary>
        public bool RxOnly = false;
        /// <summary>
        /// Whether to send grant demands on call start
        /// </summary>
        public bool TxGrantDemands = false;
    }

    /// <summary>
    /// Network Configuration
    /// </summary>
    public class NetworkConfigObject
    {
        /// <summary>
        /// Peer ID of this RC2-DVM instance
        /// </summary>
        public uint PeerId;
        /// <summary>
        /// Address of the FNE to connect to
        /// </summary>
        public string Address;
        /// <summary>
        /// Port of the FNE to connect to
        /// </summary>
        public int Port = 62031;
        /// <summary>
        /// FNE access password
        /// </summary>
        public string Password;
        /// <summary>
        /// Whether the FNE connection is encrypted
        /// </summary>
        public bool Encrypted = false;
        /// <summary>
        /// Preshared key for FNE encryption
        /// </summary>
        public string PresharedKey;
        /// <summary>
        /// Time between FNE pings
        /// </summary>
        public int PingTime = 5;
        /// <summary>
        /// Network identity for the peer
        /// </summary>
        public string Identity = "RC2DVM";
        /// <summary>
        /// Whether to send talkgroup affiliations to the master
        /// </summary>
        public bool SendChannelAffiliations = false;
        /// <summary>
        /// Whether to allow diagnostic transfers to the FNE
        /// </summary>
        public bool AllowDiagnosticTransfer = true;
        /// <summary>
        /// Whether to enable additional debug messages
        /// </summary>
        public bool Debug = false;
    }

    public class ConfigObject
    {
        /// <summary>
        /// Logging Config
        /// </summary>
        public LogConfigObject Log = new LogConfigObject();
        /// <summary>
        /// Network Config
        /// </summary>
        public NetworkConfigObject Network = new NetworkConfigObject();
        /// <summary>
        /// Configured Virtual Channels
        /// </summary>
        public List<VirtualChannelConfigObject> VirtualChannels = new List<VirtualChannelConfigObject>();
    }
}
