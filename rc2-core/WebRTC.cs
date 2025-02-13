using Serilog;
using SIPSorcery.Net;
using SIPSorcery.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;
using System.Net;
using NAudio;
using NAudio.Wave;
using NAudio.Utils;
using NAudio.Wave.SampleProviders;
using Concentus;

namespace rc2_core
{
    public class WebRTC
    {
        // Objects for RX audio processing
        private AudioEncoder RxEncoder;
        private AudioFormat RxFormat = AudioFormat.Empty;
        
        // Objects for TX audio processing
        public AudioEncoder TxEncoder;
        private AudioFormat TxFormat = AudioFormat.Empty;

        // We make separate encoders for recording since some codecs can be time-variant
        public AudioEncoder RecRxEncoder;
        private AudioEncoder RecTxEncoder;

        // TX audio output samplerate
        private int txAudioSamplerate;

        // Objects for TX/RX audio recording
        public bool Record = false;          // Whether or not recording to audio files is enabled
        public string RecPath = "";        // Folder to store recordings
        public string RecTsFmt = "yyyy-MM-dd_HHmmss";       // Timestamp format string
        public bool RecTxInProgress = false;   // Flag to indicate if a file is currently being recorded
        public bool RecRxInProgress = false;
        private float recRxGain = 1;
        private float recTxGain = 1;

        // Recording format (TODO: Make configurable)
        private WaveFormat recFormat;
        // Output wave file writers
        private WaveFileWriter recTxWriter;
        private WaveFileWriter recRxWriter;

        // WebRTC variables
        private MediaStreamTrack RtcTrack;
        private RTCPeerConnection pc;
        public string Codec { get; set; } = "G722";

        // Flag whether our radio is RX only
        public bool RxOnly {get; set;} = false;

        // Event for when the WebRTC connection connects
        public event EventHandler OnConnect;

        // Event for when the WebRTC connection closes
        public event EventHandler OnClose;

        // Callback for receiving audio from the peer connection
        private Action<short[]> TxCallback;

        public WebRTC(Action<short[]> txCallback, int txSampleRate)
        {
            // Create RX encoders
            RxEncoder = new AudioEncoder();
            RecRxEncoder = new AudioEncoder();

            // Create TX encoders if we aren't RX only
            if (!RxOnly)
            {
                TxEncoder = new AudioEncoder();
                RecTxEncoder = new AudioEncoder();
                // Bind tx audio callback
                TxCallback = txCallback;
                txAudioSamplerate = txSampleRate;
            }
        }

        /// <summary>
        /// Callback used to send pre-encoded audio samples to the peer connection
        /// </summary>
        /// <param name="durationRtpUnits"></param>
        /// <param name="encodedSamples"></param>
        public void RxAudioCallback(uint durationRtpUnits, byte[] encodedSamples)
        {
            // If we don't have a peer connection, return
            if (pc == null || RxFormat.RtpClockRate == 0)
            {
                //Log.Logger.Debug($"Ignoring RX samples, WebRTC peer not connected");
                return;
            }
            
            // Send audio
            pc.SendAudio(durationRtpUnits, encodedSamples);

            //Log.Logger.Debug($"Sent {encodedSamples.Length} ({durationRtpUnits * 1000 / RxFormat.RtpClockRate} ms) {RxFormat.Codec.ToString()} samples to WebRTC peer");
            
            // Record audio if enabled
            if (Record && recRxWriter != null)
            {
                // Decode samples to pcm
                short[] pcmSamples = RecRxEncoder.DecodeAudio(encodedSamples, RxFormat);
                // Convert to float s16
                float[] s16Samples = new float[pcmSamples.Length];
                for (int n = 0; n < pcmSamples.Length; n++)
                {
                    s16Samples[n] = pcmSamples[n] / 32768f * recRxGain;
                }
                // Add to buffer
                recRxWriter.WriteSamples(s16Samples, 0, s16Samples.Length);
            }
        }

        /// <summary>
        /// Callback used to send PCM16 samples to the peer connection
        /// </summary>
        /// <param name="durationRtpUnits"></param>
        /// <param name="pcm16Samples"></param>
        public void RxAudioCallback16(short[] pcm16Samples, uint pcmSampleRate)
        {
            // If we don't have a peer connection, return
            if (pc == null || RxFormat.RtpClockRate == 0)
            {
                //Log.Logger.Debug($"Ignoring RX samples, WebRTC peer not connected");
                return;
            }

            // Resample if needed
            if (pcmSampleRate != RxFormat.ClockRate)
            {
                short[] resampled = RxEncoder.Resample(pcm16Samples, (int)pcmSampleRate, RxFormat.ClockRate);
                byte[] encodedSamples = RxEncoder.EncodeAudio(resampled, RxFormat);
                this.RxAudioCallback((uint)encodedSamples.Length, encodedSamples);
            }
            else
            {
                byte[] encodedSamples = RxEncoder.EncodeAudio(pcm16Samples, RxFormat);
                this.RxAudioCallback((uint)encodedSamples.Length, encodedSamples);
            }
        }

        /// <summary>
        /// Decode RTP audio into PCM16 samples
        /// </summary>
        /// <param name="encodedSamples"></param>
        /// <param name="pcm16Samples"></param>
        /// <param name="pcmSampleRate"></param>
        private void decodeTxAudio(byte[] encodedSamples)
        {
            // Decode
            short[] pcm16Samples = TxEncoder.DecodeAudio(encodedSamples, TxFormat);

            // Resample if needed
            if (txAudioSamplerate != TxFormat.ClockRate)
            {
                short[] resampled = TxEncoder.Resample(pcm16Samples, TxFormat.ClockRate, txAudioSamplerate);
                TxCallback(resampled);
            }
            else
            {
                TxCallback(pcm16Samples);
            }
        }

        /// <summary>
        /// Create a new peer connection to a WebRTC endpoint and configure the audio tracks
        /// </summary>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public Task<RTCPeerConnection> CreatePeerConnection()
        {
            Log.Logger.Debug("New client connected to RTC endpoint, creating peer connection");
            
            // Create RTC configuration and peer connection
            RTCConfiguration config = new RTCConfiguration
            {
            };
            pc = new RTCPeerConnection(config);

            // Debug print of supported audio formats
            Log.Logger.Verbose("Client supported formats:");
            foreach (var format in RxEncoder.SupportedFormats)
            {
                Log.Logger.Verbose("{FormatName}", format.FormatName);
            }

            // Make sure we support the desired codec
            if (!RxEncoder.SupportedFormats.Any(f => f.FormatName == Codec))
            {
                Log.Logger.Error("Specified format {SpecFormat} not supported by audio encoder!", Codec);
                throw new ArgumentException("Invalid codec specified!");
            }

            // Set send-only or send-recieve mode based on whether we're RX only or not
            if (!RxOnly)
            {
                RtcTrack = new MediaStreamTrack(RxEncoder.SupportedFormats.Find(f => f.FormatName == Codec), MediaStreamStatusEnum.SendRecv);
                Log.Logger.Debug("Added send/recv audio track to peer connection");
            } 
            else
            {
                RtcTrack = new MediaStreamTrack(RxEncoder.SupportedFormats.Find(f => f.FormatName == Codec), MediaStreamStatusEnum.SendOnly);
                Log.Logger.Debug("Added send-only audio track to peer connection");
            }

            // Add the RX track to the peer connection
            pc.addTrack(RtcTrack);

            // Audio format negotiation callback
            pc.OnAudioFormatsNegotiated += (formats) =>
            {
                // Get the format
                RxFormat = formats.Find(f => f.FormatName == Codec);
                // Set the source to use the format
                //RxSource.SetAudioSourceFormat(RxFormat);
                Log.Logger.Debug("Negotiated RX audio format {AudioFormat} ({ClockRate}/{Chs})", RxFormat.FormatName, RxFormat.ClockRate, RxFormat.ChannelCount);
                // Set our wave and buffer writers to the proper sample rate
                recFormat = new WaveFormat(RxFormat.ClockRate, 16, 1);
                if (!RxOnly)
                {
                    TxFormat = formats.Find(f => f.FormatName == Codec);
                    //TxEndpoint.SetAudioSinkFormat(TxFormat);
                    Log.Logger.Debug("Negotiated TX audio format {AudioFormat} ({ClockRate}/{Chs})", TxFormat.FormatName, TxFormat.ClockRate, TxFormat.ChannelCount);
                }
            };

            // Connection state change callback
            pc.onconnectionstatechange += ConnectionStateChange;

            // Debug Stuff
            pc.OnReceiveReport += (re, media, rr) => Log.Logger.Verbose("RTCP report received {Media} from {RE}\n{Report}", media, re, rr.GetDebugSummary());
            pc.OnSendReport += (media, sr) => Log.Logger.Verbose("RTCP report sent for {Media}\n{Summary}", media, sr.GetDebugSummary());
            pc.GetRtpChannel().OnStunMessageSent += (msg, ep, isRelay) =>
            {
                Log.Logger.Verbose("STUN {MessageType} sent to {Endpoint}.", msg.Header.MessageType, ep);
            };
            pc.GetRtpChannel().OnStunMessageReceived += (msg, ep, isRelay) =>
            {
                Log.Logger.Verbose("STUN {MessageType} received from {Endpoint}.", msg.Header.MessageType, ep);
                //Log.Verbose(msg.ToString());
            };
            pc.oniceconnectionstatechange += (state) => Log.Verbose("ICE connection state change to {ICEState}.", state);

            // RTP Samples callback
            pc.OnRtpPacketReceived += (IPEndPoint rep, SDPMediaTypesEnum media, RTPPacket rtpPkt) =>
            {
                if (media == SDPMediaTypesEnum.audio)
                {
                    //Log.Verbose("Got RTP audio from {Endpoint} - ({length}-byte payload)", rep.ToString(), rtpPkt.Payload.Length);
                    if (!RxOnly)
                    {
                        //TxCallback(rep, media, rtpPkt);
                        decodeTxAudio(rtpPkt.Payload);
                    }
                        
                    // Save TX audio to file, if we're supposed to and the file is open
                    if (Record && recTxWriter != null)
                    {
                        // Get samples
                        byte[] samples = rtpPkt.Payload;
                        // Decode samples
                        short[] pcmSamples = RecTxEncoder.DecodeAudio(samples, TxFormat);
                        // Convert to float s16
                        float[] s16Samples = new float[pcmSamples.Length];
                        for (int n = 0; n < pcmSamples.Length; n++)
                        {
                            s16Samples[n] = pcmSamples[n] / 32768f * recTxGain;
                        }
                        // Add to buffer
                        recTxWriter.WriteSamples(s16Samples, 0, s16Samples.Length);
                    }
                }
            };

            return Task.FromResult(pc);
        }

        /// <summary>
        /// Handler for RTC connection state chagne
        /// </summary>
        /// <param name="state">the new connection state</param>
        private async void ConnectionStateChange(RTCPeerConnectionState state)
        {
            Log.Logger.Information("Peer connection state change to {PCState}.", state);

            if (state == RTCPeerConnectionState.failed)
            {
                Log.Logger.Error("Peer connection failed");
                Log.Logger.Debug("Closing peer connection");
                pc.Close("Connection failed");
            }
            else if (state == RTCPeerConnectionState.closed)
            {
                Log.Logger.Debug("WebRTC connection closed");
                if (OnClose != null)
                {
                    OnClose(this, EventArgs.Empty);
                }
            }
            else if (state == RTCPeerConnectionState.connected)
            {
                Log.Logger.Debug("WebRTC connection opened");
                if (OnConnect != null)
                {
                    OnConnect(this, EventArgs.Empty);
                }
            }
        }

        public void Stop(string reason)
        {
            Log.Logger.Warning("Stopping WebRTC with reason {Reason}", reason);
            if (pc != null)
            {
                //Log.Logger.Information($"Closing WebRTC peer connection to {pc.AudioDestinationEndPoint.ToString()}");
                pc.Close(reason);
                pc = null;
            }
            else
            {
                Log.Logger.Debug("No WebRTC peer connections to close");
            }
        }

        /// <summary>
        /// Start a wave recording with the specified file prefix
        /// </summary>
        /// <param name="prefix">filename prefix, appended with timestamp</param>
        public void RecStartTx(string name)
        {
            // Stop recording RX
            if (RecRxInProgress)
            {
                RecStop();
            }
            // Only create a new file if recording is enabled and we're not already recording TX
            if (Record && !RecTxInProgress)
            {
                // Get full filepath
                string filename = $"{RecPath}/{DateTime.Now.ToString(RecTsFmt)}_{name.Replace(' ', '_')}_TX.wav";
                // Create writer
                recTxWriter = new WaveFileWriter(filename, recFormat);
                Log.Logger.Debug("Starting new TX recording: {file}", filename);
                // Set Flag
                RecTxInProgress = true;
            }
        }

        public void RecStartRx(string name)
        {
            // Stop recording TX
            if (RecTxInProgress)
            {
                RecStop();
            }
            // Only create a new file if recording is enabled and we're not already recording RX
            if (Record && !RecRxInProgress)
            {
                // Get full filepath
                string filename = $"{RecPath}/{DateTime.Now.ToString(RecTsFmt)}_{name.Replace(' ', '_')}_RX.wav";
                // Create writer
                recRxWriter = new WaveFileWriter(filename, recFormat);
                Log.Logger.Debug("Starting new RX recording: {file}", filename);
                // Set Flag
                RecRxInProgress = true;
            }
        }

        /// <summary>
        /// Stop a wave recording
        /// </summary>
        public void RecStop()
        {
            if (recTxWriter != null)
            {
                recTxWriter.Close();
                recTxWriter = null;
            }
            if (recRxWriter != null)
            {
                recRxWriter.Close();
                recRxWriter = null;
            }
            RecTxInProgress = false;
            RecRxInProgress = false;
            Log.Logger.Debug("Stopped recording");
        }

        public void SetRecGains(double rxGainDb, double txGainDb)
        {
            recRxGain = (float)Math.Pow(10, rxGainDb/20);
            recTxGain = (float)Math.Pow(10, txGainDb/20);
        }
    }
}
