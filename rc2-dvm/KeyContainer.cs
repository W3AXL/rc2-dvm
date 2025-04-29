using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.Core.Tokens;

using fnecore.P25.KMM;

namespace rc2_dvm
{
    public class KeyEntry
    {
        public ushort KeyId { get; set; }
        public byte AlgId { get; set; }
        public string Key { get; set; }

        /// <summary>
        /// Gets the contents of the Key property as a byte[]
        /// </summary>
        public byte[] KeyBytes => string.IsNullOrEmpty(Key) ? [] : StringToByteArray(Key);

        /// <summary>
        /// Returns a key item in the FNECore key format
        /// </summary>
        /// <returns></returns>
        public KeyItem GetKeyItem()
        {
            var keyItem = new KeyItem();
            keyItem.KeyId = KeyId;
            keyItem.KeyFormat = AlgId;
            keyItem.SetKey(KeyBytes, (uint)KeyBytes.Length);
            return keyItem;
        }

        private static byte[] StringToByteArray(string hex)
        {
            return Enumerable.Range(0, hex.Length)
                .Where(x => x % 2 == 0)
                .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                .ToArray();
        }
    }

    public class KeyContainer
    {
        /// <summary>
        /// Keys in this container
        /// </summary>
        public List<KeyEntry> Keys { get; set; } = [];

        /// <summary>
        /// Return a key in the key container by its key ID, in the FNECore format
        /// </summary>
        /// <param name="keyId"></param>
        /// <returns>KeyItem, or null if not found</returns>
        public KeyItem GetKeyById(ushort keyId)
        {
            KeyEntry key = Keys.FirstOrDefault(k => k.KeyId == keyId);
            if (key != null)
                return key.GetKeyItem();
            else
                return null;
        }
    }
}
