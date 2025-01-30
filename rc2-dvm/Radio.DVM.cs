using System;
using System.Collections.Generic;
using System.Linq;
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

        public DVMRadio(string name, bool rxOnly) : base(name, "", rxOnly, DVMSoftkeys)
        {
            
        }
    }
}
