using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using SIPSorcery.Net;
using SIPSorcery.Media;
using WebSocketSharp.Server;
using SIPSorceryMedia.Abstractions;
using WebSocketSharp;
using Newtonsoft.Json;

namespace rc2_core
{
    public class RC2Server
    {
        private WebSocketServer wss {  get; set; }

        private WebRTC rtc { get; set; }

        private Radio radio { get; set; }

        // Flags for TX/RX recording
        public bool TxRecording
        {
            get
            {
                return rtc.RecTxInProgress;
            }
        }
        public bool RxRecording
        {
            get
            {
                return rtc.RecRxInProgress;
            }
        }

        public RC2Server(IPAddress address, int port, Radio _radio, Action<short[]> txAudioCallback, int txAudioSampleRate)
        {
            wss = new WebSocketServer(address, port);
            rtc = new WebRTC(txAudioCallback, txAudioSampleRate);
            radio = _radio;
            // Bind status callback
            radio.StatusCallback += SendRadioStatus;
        }

        public void Start()
        {
            Serilog.Log.Logger.Information($"Starting websocket server on {wss.Address}:{wss.Port}");
            // Set up the WebRTC handler
            wss.AddWebSocketService<WebRTCWebSocketPeer>("/rtc", (peer) => peer.CreatePeerConnection = rtc.CreatePeerConnection);
            // Set up the regular message handler
            wss.AddWebSocketService<ConsoleBehavior>("/", () => new ConsoleBehavior(this, this.radio));
            // Keeps the thing alive
            wss.KeepClean = false;
            // Start the service
            wss.Start();
        }

        public void StopRTC(string reason)
        {
            rtc.Stop(reason);
        }

        public void Stop(string reason)
        {
            rtc.Stop(reason);
            wss.Stop();
        }

        public void SendRadioStatus()
        {
            string statusJson = radio.Status.Encode();
            Log.Debug("Sending radio status via websocket");
            Log.Verbose(statusJson);
            SendClientMessage("{\"status\": " + statusJson + " }");
        }

        public void SendClientMessage(string msg)
        {
            wss.WebSocketServices["/"].Sessions.Broadcast(msg);
        }

        public void SendAck(String cmd = "")
        {
            wss.WebSocketServices["/"].Sessions.Broadcast($"{{\"ack\": \"{cmd}\"}}");
        }

        public void SendNack(String cmd = "")
        {
            wss.WebSocketServices["/"].Sessions.Broadcast($"{{\"nack\": \"{cmd}\"}}");
        }

        public void RecordTx(string filename)
        {
            rtc.RecStartTx(filename);
        }

        public void RecordRx(string filename)
        {
            rtc.RecStartRx(filename);
        }

        public void RecordStop()
        {
            rtc.RecStop();
        }

        // WebRTC audio functions
        public void RxSendPCM16Samples(short[] samples, uint samplerate)
        {
            rtc.RxAudioCallback16(samples, samplerate);
        }
    }

    internal class ConsoleBehavior : WebSocketBehavior
    {
        private RC2Server server;
        private Radio radio;
        public ConsoleBehavior(RC2Server _server, Radio _radio)
        {
            server = _server;
            radio = _radio;
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            var msg = e.Data;

            if (msg == null) { return; }

            Serilog.Log.Verbose($"Got client message from websocket: {msg}");
            dynamic jsonObj = JsonConvert.DeserializeObject(msg);

            if (jsonObj == null)
            {
                Serilog.Log.Logger.Warning("Unable to decode data from websocket!");
                return; 
            }

            // Handle commands
            if (jsonObj.ContainsKey("radio"))
            {
                // Radio Status Query
                if (jsonObj.radio.command == "query")
                {
                    server.SendRadioStatus();
                }
                // Radio Start Transmit Command
                else if (jsonObj.radio.command == "startTx")
                {
                    if (radio.SetTransmit(true))
                        server.SendAck("startTx");
                    else
                        server.SendNack("startTx");
                }
                // Radio Stop Transmit Command
                else if (jsonObj.radio.command == "stopTx")
                {
                    if (radio.SetTransmit(false))
                        server.SendAck("stopTx");
                    else
                        server.SendNack("stopTx");
                }
                // Channel Up/Down
                else if (jsonObj.radio.command == "chanUp")
                {
                    if (radio.ChangeChannel(false))
                        server.SendAck("chanUp");
                    else
                        server.SendNack("chanUp");
                }
                else if (jsonObj.radio.command == "chanDn")
                {
                    if (radio.ChangeChannel(true))
                        server.SendAck("chanDn");
                    else
                        server.SendNack("chanDn");
                }
                // Button press/release
                else if (jsonObj.radio.command == "buttonPress")
                {
                    if (radio.PressButton((SoftkeyName)Enum.Parse(typeof(SoftkeyName),(string)jsonObj.radio.options)))
                        server.SendAck("buttonPress");
                    else
                        server.SendNack("buttonPress");
                }
                else if (jsonObj.radio.command == "buttonRelease")
                {
                    if (radio.ReleaseButton((SoftkeyName)Enum.Parse(typeof(SoftkeyName),(string)jsonObj.radio.options)))
                        server.SendAck("buttonRelease");
                    else
                        server.SendNack("buttonRelease");
                }
                // Reset
                else if (jsonObj.radio.command == "reset")
                {
                    Serilog.Log.Information("Resetting and restarting radio interface");
                    // Stop
                    radio.Stop();
                    // Restart with reset
                    radio.Start(true);
                }
            }
        }

        protected override void OnClose(CloseEventArgs e)
        {
            Serilog.Log.Warning("Websocket connection closed: {args}", e.Reason);
            server.StopRTC("Websocket closed");
        }

        protected override void OnError(WebSocketSharp.ErrorEventArgs e)
        {
            Serilog.Log.Error("Websocket encountered an error! {error}", e.Message);
        }
    }
}
