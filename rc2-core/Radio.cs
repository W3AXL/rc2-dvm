using Serilog;
using Newtonsoft.Json;
using System.Net;
using SIPSorcery.Net;

namespace rc2_core
{
    // Valid states a radio can be in
    public enum RadioState
    {
        Disconnected,
        Connecting,
        Idle,
        Transmitting,
        Receiving,
        Error,
        Disconnecting
    }

    /// <summary>
    /// Valid scanning states (used for scan icons on radio cards in the client)
    /// </summary>
    public enum ScanState
    {
        NotScanning,
        Scanning
    }

    public enum PriorityState
    {
        NoPriority,
        Priority1,
        Priority2
    }

    public enum PowerState
    {
        LowPower,
        MidPower,
        HighPower
    }

    public enum SoftkeyState
    {
        Off,
        On,
        Flashing
    }

    /// <summary>
    /// These are the valid softkey bindings which can be used to setup softkeys on radios which don't have them
    /// </summary>
    /// Pruned from the Astro25 mobile CPS help section on button bindings
    public enum SoftkeyName
    {
        CALL,   // Signalling call
        CHAN,   // Channel Select
        CHUP,   // Channel Up
        CHDN,   // Channel Down
        DEL,    // Nuisance Delete
        DIR,    // Talkaround/direct
        EMER,   // Emergency
        DYNP,   // Dynamic Priority
        HOME,   // Home
        LOCK,   // Trunking site lock
        LPWR,   // Low power
        MON,    // Monitor (PL defeat)
        PAGE,   // Signalling page
        PHON,   // Phone operation
        RAB1,   // Repeater access button 1
        RAB2,   // Repeater access button 2
        RCL,    // Scan recall
        SCAN,   // Scan mode, etc
        SEC,    // Secure mode
        SEL,    // Select
        SITE,   // Site alias
        TCH1,   // One-touch 1
        TCH2,   // One-touch 2
        TCH3,   // One-touch 3
        TCH4,   // One-touch 4
        TGRP,   // Talkgroup select
        TMS,    // Text messaging
        TMSQ,   // Quick message
        ZNUP,   // Zone up
        ZNDN,   // Zone down
        ZONE,   // Zone select
    }

    /// <summary>
    /// Softkey object to hold key text, description (for hover) and state
    /// </summary>
    public class Softkey
    {
        public SoftkeyName Name { get; set; }
        public string Description { get; set; }
        public SoftkeyState State { get; set; }

        public Softkey(SoftkeyName name, string description = "")
        {
            Name = name;
            Description = description;
            State = SoftkeyState.Off;
        }
    }

    /// <summary>
    /// Class for text-replacement lookup objects
    /// </summary>
    public class TextLookup
    {
        // The text string to match
        public string Match { get; set; }
        // The text string to replace the matched text with
        public string Replacement { get; set; }

        public TextLookup(string match, string replacement)
        {
            Match = match;
            Replacement = replacement;
        }
    }

    /// <summary>
    /// Radio status object, contains all the possible radio states sent to the client during status updates
    /// </summary>
    public class RadioStatus
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string ZoneName { get; set; } = "";
        public string ChannelName { get; set; } = "";
        public RadioState State { get; set; } = RadioState.Disconnected;
        public ScanState ScanState { get; set; } = ScanState.NotScanning;
        public PriorityState PriorityState {get; set;} = PriorityState.NoPriority;
        public PowerState PowerState {get; set;} = PowerState.LowPower;
        public List<Softkey> Softkeys { get; set; } = new List<Softkey>();
        public bool Monitor { get; set; } = false;
        public bool Direct {get; set;} = false;
        public bool Error { get; set; } = false;
        public string ErrorMsg { get; set; } = "";

        /// <summary>
        /// Encode the RadioStatus object into a JSON string for sending to the client
        /// </summary>
        /// <returns></returns>
        public string Encode()
        {
            // convert the status object to a string
            return JsonConvert.SerializeObject(this, new Newtonsoft.Json.Converters.StringEnumConverter());
        }
    }

    /// <summary>
    /// Radio class representing a radio to be controlled by the daemon
    /// </summary>
    public abstract class Radio
    {
        // Radio Name
        private string name = "Radio";
        public string Name
        {
            get { return name; }
        }

        // Radio Description
        private string desc = "RC2 Base Radio Instance";
        public string Desc
        {
            get { return desc; }
        }

        // Whether the radio is RX only
        public bool RxOnly { get; set; }

        // Radio status
        public RadioStatus Status { get; set; }

        // Lookup lists for zone & channel text
        private List<TextLookup> ZoneLookups = new List<TextLookup>();
        private List<TextLookup> ChanLookups = new List<TextLookup>();

        public delegate void Callback();
        public Callback StatusCallback { get; set; }

        public Action<short[], int> TxAudioCallback;

        public int RecTimeout { get; set; } = 0;

        // RC2 server instance
        private RC2Server server { get; set; }

        /// <summary>
        /// Overload for a basic radio that does nothing
        /// </summary>
        /// <param name="type"></param>
        /// <param name="rxOnly"></param>
        /// <param name="zoneName"></param>
        /// <param name="channelName"></param>
        public Radio(
            string name, string desc, bool rxOnly,
            IPAddress listenAddress, int listenPort,
            List<Softkey> softkeys = null,
            List<TextLookup> zoneLookups = null,
            List<TextLookup> chanLookups = null,
            Action<short[]> txAudioCallback = null, int txAudioSampleRate = 8000)
        {
            // Log Print
            Log.Logger.Information($"Creating new RC2 radio {name} ({desc}) listening on {listenAddress}:{listenPort}");

            // Base name
            this.name = name;
            this.desc = desc;

            // Create backend server
            server = new RC2Server(listenAddress, listenPort, this, txAudioCallback, txAudioSampleRate);

            // Create status and assign name & description
            Status = new RadioStatus();
            Status.Name = name;
            Status.Description = desc;
            // Set RX Only
            RxOnly = true;
            // Save softkeys
            if (softkeys != null) { Status.Softkeys = softkeys; }
            // Save lookups
            if (zoneLookups != null) { ZoneLookups = zoneLookups; }
            if (chanLookups != null) { ChanLookups = chanLookups; }
        }

        /// <summary>
        /// Start the radio
        /// </summary>
        /// <param name="reset">Whether to reset the radio or not</param>
        public void Start(bool reset = false)
        {
            Log.Logger.Information($"Starting radio {name}");
            // Start the server
            server.Start();
            // Update the radio status to connecting
            Status.State = RadioState.Connecting;
            // Call the status callback to set up initial status
            RadioStatusCallback();
        }

        /// <summary>
        /// Stop the radio
        /// </summary>
        public void Stop()
        {
            Log.Logger.Information($"Stopping radio {name}");
            // Stop the server
            server.Stop("Radio instance stopped");
        }

        /// <summary>
        /// Callback function called by the interface class, which in turn calls the callback in the main program for reporting status
        /// Confusing, I know
        /// Basically it goes like this (for SB9600) SB9600.StatusCallback() -> Radio.RadioStatusCallback() -> DaemonWebsocket.SendRadioStatus()
        /// </summary>
        private void RadioStatusCallback()
        {
            Log.Logger.Verbose("Got radio status callback from interface");
            // Perform lookups on zone/channel names (radio-control-type agnostic)
            if (ZoneLookups.Count > 0)
            {
                foreach (TextLookup lookup in ZoneLookups)
                {
                    // An empty string for the match indicates we should always replace the zone name with the replacement
                    if (lookup.Match == "")
                    {
                        Log.Logger.Verbose("Empty lookup {replacement} found for zone name, overriding all other lookups", lookup.Replacement);
                        Status.ZoneName = lookup.Replacement;
                        break;
                    }
                    if (Status.ZoneName.Contains(lookup.Match))
                    {
                        Log.Logger.Verbose("Found zone text {ZoneName} from {Match} in original text {Text}", lookup.Replacement, lookup.Match, Status.ZoneName);
                        Status.ZoneName = lookup.Replacement;
                    }
                }
            }
            if (ChanLookups.Count > 0)
            {
                foreach (TextLookup lookup in ChanLookups)
                {
                    if (Status.ChannelName.Contains(lookup.Match))
                    {
                        Log.Logger.Verbose("Found channel text {ChannelName} from {Match} in original text {Text}", lookup.Replacement, lookup.Match, Status.ChannelName);
                        Status.ChannelName = lookup.Replacement;
                    }
                }
            }
            // Call recording start/stop callbacks which will trigger audio recording file start/stop if enabled
            if (Status.State == RadioState.Transmitting)
            {
                Task.Delay(100).ContinueWith(t => RecTxCallback());
            }
            else if (Status.State == RadioState.Receiving)
            {
                Task.Delay(100).ContinueWith(t => RecRxCallback());
            }
            // Stop recording if we're not either of the above
            else
            {
                if (server.RxRecording || server.TxRecording)
                {
                    Task.Delay(RecTimeout).ContinueWith(t => RecStopCallback());
                }
            }
            // Call the next callback up
            StatusCallback();
        }

        /// <summary>
        /// Sets transmit state of the connected radio
        /// </summary>
        /// <param name="tx">true to transmit, false to stop</param>
        /// <returns>true on success</returns>
        public abstract bool SetTransmit(bool tx);

        /// <summary>
        /// Change the radio's channel
        /// </summary>
        /// <param name="down"></param>
        /// <returns></returns>
        public abstract bool ChangeChannel(bool down);

        /// <summary>
        /// Press a button
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public abstract bool PressButton(SoftkeyName name);

        /// <summary>
        /// Release a button
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public abstract bool ReleaseButton(SoftkeyName name);

        /// <summary>
        /// Callback for transmit audio recording
        /// </summary>
        private void RecTxCallback()
        {
            server.RecordTx(Status.ChannelName.Trim());
        }

        /// <summary>
        /// Callback for receive audio recording
        /// </summary>
        private void RecRxCallback()
        {
            server.RecordRx(Status.ChannelName.Trim());
        }

        /// <summary>
        /// Stop recording callback
        /// </summary>
        private void RecStopCallback()
        {
            // Stop TX recording if we're not transmitting
            if (server.TxRecording && Status.State != RadioState.Transmitting)
            {
                server.RecordStop();
            }
            // Stop RX recording if we're not receiving
            if (server.RxRecording && Status.State != RadioState.Receiving)
            {
                server.RecordStop();
            }
        }

        /// <summary>
        /// Send PCM16 samples to the WebRTC connection for encoding and transmission to the console
        /// </summary>
        /// <param name="samples">array of PCM16 samples</param>
        public void RxSendPCM16Samples(short[] samples, uint samplerate)
        {
            server.RxSendPCM16Samples(samples, samplerate);
        }


    }
}
