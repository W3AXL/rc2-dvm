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
    public class ConsoleServer
    {
        private WebSocketServer wss {  get; set; }

        public Radio RadioObj { get; set; }

        private ConsoleBehavior cb { get; set; }

        public ConsoleServer(IPAddress address, int port, Radio _radio)
        {
            wss = new WebSocketServer(address, port);
            RadioObj = _radio;
            // Bind status callback
            RadioObj.StatusCallback += SendRadioStatus;
        }

        public void Start()
        {
            Serilog.Log.Logger.Information($"Starting websocket server on {wss.Address}:{wss.Port}");
            // Set up the WebRTC handler
            wss.AddWebSocketService<WebRTCWebSocketPeer>("/rtc", (peer) => peer.CreatePeerConnection = WebRTC.CreatePeerConnection);
            // Set up the regular message handler
            wss.AddWebSocketService<ConsoleBehavior>("/", () => new ConsoleBehavior(this));
            // Keeps the thing alive
            wss.KeepClean = false;
            // Start the service
            wss.Start();
        }

        public void Stop()
        {
            wss.Stop();
        }

        public void SendRadioStatus()
        {
            string statusJson = RadioObj.Status.Encode();
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
    }

    internal class ConsoleBehavior : WebSocketBehavior
    {
        private ConsoleServer consoleServer;
        public ConsoleBehavior(ConsoleServer server)
        {
            consoleServer = server;
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
                    consoleServer.SendRadioStatus();
                }
                // Radio Start Transmit Command
                else if (jsonObj.radio.command == "startTx")
                {
                    if (consoleServer.RadioObj.SetTransmit(true))
                        consoleServer.SendAck("startTx");
                    else
                        consoleServer.SendNack("startTx");
                }
                // Radio Stop Transmit Command
                else if (jsonObj.radio.command == "stopTx")
                {
                    if (consoleServer.RadioObj.SetTransmit(false))
                        consoleServer.SendAck("stopTx");
                    else
                        consoleServer.SendNack("stopTx");
                }
                // Channel Up/Down
                else if (jsonObj.radio.command == "chanUp")
                {
                    if (consoleServer.RadioObj.ChangeChannel(false))
                        consoleServer.SendAck("chanUp");
                    else
                        consoleServer.SendNack("chanUp");
                }
                else if (jsonObj.radio.command == "chanDn")
                {
                    if (consoleServer.RadioObj.ChangeChannel(true))
                        consoleServer.SendAck("chanDn");
                    else
                        consoleServer.SendNack("chanDn");
                }
                // Button press/release
                else if (jsonObj.radio.command == "buttonPress")
                {
                    if (consoleServer.RadioObj.PressButton((SoftkeyName)Enum.Parse(typeof(SoftkeyName),(string)jsonObj.radio.options)))
                        consoleServer.SendAck("buttonPress");
                    else
                        consoleServer.SendNack("buttonPress");
                }
                else if (jsonObj.radio.command == "buttonRelease")
                {
                    if (consoleServer.RadioObj.ReleaseButton((SoftkeyName)Enum.Parse(typeof(SoftkeyName),(string)jsonObj.radio.options)))
                        consoleServer.SendAck("buttonRelease");
                    else
                        consoleServer.SendNack("buttonRelease");
                }
                // Reset
                else if (jsonObj.radio.command == "reset")
                {
                    Serilog.Log.Information("Resetting and restarting radio interface");
                    // Stop
                    consoleServer.RadioObj.Stop();
                    // Restart with reset
                    consoleServer.RadioObj.Start();
                }
            }
        }

        protected override void OnClose(CloseEventArgs e)
        {
            Serilog.Log.Warning("Websocket connection closed: {args}", e.Reason);
            WebRTC.Stop("Websocket closed");
            //DaemonWebsocket.radio.Stop();
        }

        protected override void OnError(WebSocketSharp.ErrorEventArgs e)
        {
            Serilog.Log.Error("Websocket encountered an error! {error}", e.Message);
        }
    }
}
