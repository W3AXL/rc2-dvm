using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using rc2_core;
using rc2_dvm;

namespace rc2_dvm
{
    internal class DVMRadio : Radio
    {
        // Fixed softkeys for a DVM channel
        internal static List<Softkey> DVMSoftkeys = new List<Softkey>
        {
            new Softkey(SoftkeyName.SCAN, "Virtual Talkgroup Scanning")
        };

        // Talkgroup list
        private List<TalkgroupConfigObject> talkgroups;

        // Virtual Channel Object
        private VirtualChannel vChannel;

        /// <summary>
        /// Constructs a new DVM radio instance
        /// </summary>
        /// <param name="name">Name of this radio</param>
        /// <param name="rxOnly">whether this radio is RX only</param>
        /// <param name="listenAddress">server listen address</param>
        /// <param name="listenPort">server listen port</param>
        /// <param name="talkgroups">available talkgroups</param>
        /// <param name="vChannel">virtual channel instance associated with this radio</param>
        public DVMRadio(
            string name, bool rxOnly,
            IPAddress listenAddress, int listenPort,
            List<TalkgroupConfigObject> talkgroups, VirtualChannel vChannel, 
            Action<short[]> txAudioCallback, int txAudioSampleRate) : base(name, "", rxOnly, listenAddress, listenPort, DVMSoftkeys, null, null, txAudioCallback, txAudioSampleRate)
        {
            this.talkgroups = talkgroups;
            this.vChannel = vChannel;
        }

        /// <summary>
        /// Increment or decrement the currently selected talkgroup
        /// </summary>
        /// <param name="down"></param>
        /// <returns></returns>
        public override bool ChangeChannel(bool down)
        {
            if (down)
            {
                return vChannel.ChannelDown();
            }
            else
            {
                return vChannel.ChannelUp();
            }
        }

        /// <summary>
        /// Set transmit on the DVM radio
        /// </summary>
        /// <param name="tx">whether to TX or not</param>
        /// <returns></returns>
        public override bool SetTransmit(bool tx)
        {
            if (tx)
            {
                return vChannel.StartTransmit();
            }
            else
            {
                return vChannel.StopTransmit();
            }
        }

        public override bool PressButton(SoftkeyName name)
        {
            // TODO: translate softkeys into actions
            return false;
        }

        public override bool ReleaseButton(SoftkeyName name)
        {
            // TODO: same as above
            return false;
        }
    }
}
