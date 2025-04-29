// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Audio Bridge
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Audio Bridge
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2023 Bryan Biedenkapp, N2PLL
*   Copyright (C) 2024 Caleb, KO4UYJ
*
*/
using System;
using System.Net;
using System.Collections.Generic;
using System.Text;
using System.Net.Sockets;
using System.Threading.Tasks;

using Serilog;

using fnecore;
using rc2_dvm;

namespace rc2_dvm
{
    /// <summary>
    /// Implements a peer FNE router system.
    /// </summary>
    public class PeerSystem : FneSystemBase
    {
        public FnePeer peer;

        /*
        ** Methods
        */

        /// <summary>
        /// Initializes a new instance of the <see cref="PeerSystem"/> class.
        /// </summary>
        public PeerSystem() : base(Create())
        {
            peer = (FnePeer)fne;
        }

        /// <summary>
        /// Internal helper to instantiate a new instance of <see cref="FnePeer"/> class.
        /// </summary>
        /// <param name="config">Peer stanza configuration</param>
        /// <returns><see cref="FnePeer"/></returns>
        private static FnePeer Create()
        {
            IPEndPoint endpoint = new IPEndPoint(IPAddress.Any, RC2DVM.Configuration.Network.Port);
            string presharedKey = RC2DVM.Configuration.Network.Encrypted ? RC2DVM.Configuration.Network.PresharedKey : null;

            if (RC2DVM.Configuration.Network.Address == null)
                throw new NullReferenceException("address");
            if (RC2DVM.Configuration.Network.Address == string.Empty)
                throw new ArgumentException("address");

            // handle using address as IP or resolving from hostname to IP
            try
            {
                endpoint = new IPEndPoint(IPAddress.Parse(RC2DVM.Configuration.Network.Address), RC2DVM.Configuration.Network.Port);
            }
            catch (FormatException)
            {
                IPAddress[] addresses = Dns.GetHostAddresses(RC2DVM.Configuration.Network.Address);
                if (addresses.Length > 0)
                    endpoint = new IPEndPoint(addresses[0], RC2DVM.Configuration.Network.Port);
            }

            Log.Logger.Information("    Peer ID:              {peerId}", RC2DVM.Configuration.Network.PeerId);
            Log.Logger.Information("    Master Addresss:      {address:l}", RC2DVM.Configuration.Network.Address);
            Log.Logger.Information("    Master Port:          {port}", RC2DVM.Configuration.Network.Port);
            Log.Logger.Information("    Identity:             {identity:l}", RC2DVM.Configuration.Network.Identity);
            Log.Logger.Information("    Channel Affiliations: {chanaff}", RC2DVM.Configuration.Network.SendChannelAffiliations);
            Log.Logger.Information("    Diagnostic Transfer:  {diagxfer}", RC2DVM.Configuration.Network.AllowDiagnosticTransfer);
            Log.Logger.Information("    Debug:                {debug}", RC2DVM.Configuration.Network.Debug);

            FnePeer peer = new FnePeer(RC2DVM.Configuration.Network.Identity, RC2DVM.Configuration.Network.PeerId, endpoint, presharedKey);

            // set configuration parameters
            peer.Passphrase = RC2DVM.Configuration.Network.Password;
            peer.PingTime = RC2DVM.Configuration.Network.PingTime;
            peer.Information.Details = new PeerDetails
            {
                Identity = RC2DVM.Configuration.Network.Identity,
                Software = RC2DVM.SWVersionShort,
                ConventionalPeer = !RC2DVM.Configuration.Network.SendChannelAffiliations,   // If we're not set up to send affiliations, we identify as a conventional peer
            };

            peer.PeerConnected += Peer_PeerConnected;

            return peer;
        }

        /// <summary>
        /// Event action that handles when a peer connects.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        /// <exception cref="NotImplementedException"></exception>
        private static void Peer_PeerConnected(object sender, PeerConnectedEvent e)
        {
            // Send group affiliations for any 
            /*FnePeer peer = (FnePeer)sender;
            peer.SendMasterGroupAffiliation(1, (uint)Program.Configuration.DestinationId);*/

            // NOTE: Group Affiliations are now handled in each virtual channel
        }

        protected override void KeyResponse(object sender, KeyResponseEvent e)
        {
            // Stub
        }

        /// <summary>
        /// Start UDP audio listener
        /// </summary>
        public override async Task StartListeningAsync()
        {
            
        }

        /// <summary>
        /// Helper to send a activity transfer message to the master.
        /// </summary>
        /// <param name="message">Message to send</param>
        public void SendActivityTransfer(string message)
        {
            /* stub */
        }

        /// <summary>
        /// Helper to send a diagnostics transfer message to the master.
        /// </summary>
        /// <param name="message">Message to send</param>
        public void SendDiagnosticsTransfer(string message)
        {
            /* stub */
        }
    } // public class PeerSystem
} // namespace rc2_dvm
