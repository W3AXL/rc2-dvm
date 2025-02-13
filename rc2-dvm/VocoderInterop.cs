using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using Serilog;

namespace rc2_dvm
{

    public enum MBE_MODE
    {
        DMR_AMBE,    //! DMR AMBE
        IMBE_88BIT,  //! 88-bit IMBE (P25)
    }

    /// <summary>
    /// Wrapper class for the c++ dvmvocoder encoder library
    /// </summary>
    /// Using info from https://stackoverflow.com/a/315064/1842613
    public class MBEEncoder
    {
        /// <summary>
        /// Create a new MBEEncoder
        /// </summary>
        /// <returns></returns>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr MBEEncoder_Create(MBE_MODE mode);

        /// <summary>
        /// Encode PCM16 samples to MBE codeword
        /// </summary>
        /// <param name="samples">Input PCM samples</param>
        /// <param name="codeword">Output MBE codeword</param>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        public static extern void MBEEncoder_Encode(IntPtr pEncoder, [In] Int16[] samples, [Out] byte[] codeword);

        /// <summary>
        /// Encode MBE to bits
        /// </summary>
        /// <param name="pEncoder"></param>
        /// <param name="bits"></param>
        /// <param name="codeword"></param>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        public static extern void MBEEncoder_EncodeBits(IntPtr pEncoder, [In] char[] bits, [Out] byte[] codeword);

        /// <summary>
        /// Delete a created MBEEncoder
        /// </summary>
        /// <param name="pEncoder"></param>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        public static extern void MBEEncoder_Delete(IntPtr pEncoder);

        /// <summary>
        /// Pointer to the encoder instance
        /// </summary>
        private IntPtr encoder { get; set; }

        /// <summary>
        /// Create a new MBEEncoder instance
        /// </summary>
        /// <param name="mode">Vocoder Mode (DMR or P25)</param>
        public MBEEncoder(MBE_MODE mode)
        {
            encoder = MBEEncoder_Create(mode);
        }
        
        /// <summary>
        /// Private class destructor properly deletes interop instance
        /// </summary>
        ~MBEEncoder()
        {
            MBEEncoder_Delete(encoder);
        }

        /// <summary>
        /// Encode PCM16 samples to MBE codeword
        /// </summary>
        /// <param name="samples"></param>
        /// <param name="codeword"></param>
        public void encode([In] Int16[] samples, [Out] byte[] codeword)
        {
            MBEEncoder_Encode(encoder, samples, codeword);
        }

        public void encodeBits([In] char[] bits, [Out] byte[] codeword)
        {
            MBEEncoder_EncodeBits(encoder, bits, codeword);
        }
    }

    /// <summary>
    /// Wrapper class for the c++ dvmvocoder decoder library
    /// </summary>
    public class MBEDecoder
    {
        /// <summary>
        /// Create a new MBEDecoder
        /// </summary>
        /// <returns></returns>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr MBEDecoder_Create(MBE_MODE mode);

        /// <summary>
        /// Decode MBE codeword to samples
        /// </summary>
        /// <param name="samples">Input PCM samples</param>
        /// <param name="codeword">Output MBE codeword</param>
        /// <returns>Number of decode errors</returns>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        public static extern Int32 MBEDecoder_Decode(IntPtr pDecoder, [In] byte[] codeword, [Out] Int16[] samples);

        /// <summary>
        /// Decode MBE to bits
        /// </summary>
        /// <param name="pDecoder"></param>
        /// <param name="codeword"></param>
        /// <param name="mbeBits"></param>
        /// <returns></returns>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        public static extern Int32 MBEDecoder_DecodeBits(IntPtr pDecoder, [In] byte[] codeword, [Out] char[] bits);

        /// <summary>
        /// Delete a created MBEDecoder
        /// </summary>
        /// <param name="pDecoder"></param>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        public static extern void MBEDecoder_Delete(IntPtr pDecoder);

        /// <summary>
        /// Pointer to the decoder instance
        /// </summary>
        private IntPtr decoder { get; set; }

        /// <summary>
        /// Create a new MBEDecoder instance
        /// </summary>
        /// <param name="mode">Vocoder Mode (DMR or P25)</param>
        public MBEDecoder(MBE_MODE mode)
        {
            decoder = MBEDecoder_Create(mode);
        }

        /// <summary>
        /// Private class destructor properly deletes interop instance
        /// </summary>
        ~MBEDecoder()
        {
            MBEDecoder_Delete(decoder);
        }

        /// <summary>
        /// Decode MBE codeword to PCM16 samples
        /// </summary>
        /// <param name="samples"></param>
        /// <param name="codeword"></param>
        public Int32 decode([In] byte[] codeword, [Out] Int16[] samples)
        {
            return MBEDecoder_Decode(decoder, codeword, samples);
        }

        /// <summary>
        /// Decode MBE codeword to bits
        /// </summary>
        /// <param name="codeword"></param>
        /// <param name="bits"></param>
        /// <returns></returns>
        public Int32 decodeBits([In] byte[] codeword, [Out] char[] bits)
        {
            return MBEDecoder_DecodeBits(decoder, codeword, bits);
        }
    }

    public class MBEInterleaver
    {
        public const int PCM_SAMPLES = 160;
        public const int AMBE_CODEWORD_SAMPLES = 9;
        public const int AMBE_CODEWORD_BITS = 49;
        public const int IMBE_CODEWORD_SAMPLES = 11;
        public const int IMBE_CODEWORD_BITS = 88;

        private MBE_MODE mode;

        private MBEEncoder encoder;
        private MBEDecoder decoder;

        public MBEInterleaver(MBE_MODE mode)
        {
            this.mode = mode;
            encoder = new MBEEncoder(this.mode);
            decoder = new MBEDecoder(this.mode);
        }

        public Int32 Decode([In] byte[] codeword, [Out] byte[] mbeBits)
        {
            // Input validation
            if (codeword == null)
            {
                throw new NullReferenceException("Input MBE codeword is null!");
            }

            char[] bits = null;

            // Set up based on mode
            if (mode == MBE_MODE.DMR_AMBE)
            {
                if (codeword.Length != AMBE_CODEWORD_SAMPLES)
                {
                    throw new ArgumentOutOfRangeException($"AMBE codeword length is != {AMBE_CODEWORD_SAMPLES}");
                }
                bits = new char[AMBE_CODEWORD_BITS];
            }
            else if (mode == MBE_MODE.IMBE_88BIT)
            {
                if (codeword.Length != IMBE_CODEWORD_SAMPLES)
                {
                    throw new ArgumentOutOfRangeException($"IMBE codeword length is != {IMBE_CODEWORD_SAMPLES}");
                }
                bits = new char[IMBE_CODEWORD_BITS];
            }

            if (bits == null)
            {
                throw new NullReferenceException("Failed to initialize decoder");
            }

            // Decode
            int errs = decoder.decodeBits(codeword, bits);

            // Copy
            if (mode == MBE_MODE.DMR_AMBE)
            {
                // Copy bits
                mbeBits = new byte[AMBE_CODEWORD_BITS];
                Array.Copy(bits, mbeBits, AMBE_CODEWORD_BITS);

            }
            else if (mode == MBE_MODE.IMBE_88BIT)
            {
                // Copy bits
                mbeBits = new byte[IMBE_CODEWORD_BITS];
                Array.Copy(bits, mbeBits, IMBE_CODEWORD_BITS);
            }

            return errs;
        }

        public void Encode([In] byte[] mbeBits, [Out] byte[] codeword)
        {
            if (mbeBits == null)
            {
                throw new NullReferenceException("Input MBE bit array is null!");
            }

            char[] bits = null;

            // Set up based on mode
            if (mode == MBE_MODE.DMR_AMBE)
            {
                if (mbeBits.Length != AMBE_CODEWORD_BITS)
                {
                    throw new ArgumentOutOfRangeException($"AMBE codeword bit length is != {AMBE_CODEWORD_BITS}");
                }
                bits = new char[AMBE_CODEWORD_BITS];
                Array.Copy(mbeBits, bits, AMBE_CODEWORD_BITS);
            }
            else if (mode == MBE_MODE.IMBE_88BIT)
            {
                if (mbeBits.Length != IMBE_CODEWORD_BITS)
                {
                    throw new ArgumentOutOfRangeException($"IMBE codeword bit length is != {AMBE_CODEWORD_BITS}");
                }
                bits = new char[IMBE_CODEWORD_BITS];
                Array.Copy(mbeBits, bits, IMBE_CODEWORD_BITS);
            }

            if (bits == null)
            {
                throw new ArgumentException("Bit array did not get set up properly!");
            }

            // Encode samples
            if (mode == MBE_MODE.DMR_AMBE)
            {
                // Create output array
                byte[] codewords = new byte[AMBE_CODEWORD_SAMPLES];
                // Encode
                encoder.encodeBits(bits, codewords);
                // Copy
                codeword = new byte[AMBE_CODEWORD_SAMPLES];
                Array.Copy(codewords, codeword, IMBE_CODEWORD_SAMPLES);
            }
            else if (mode == MBE_MODE.IMBE_88BIT)
            {
                // Create output array
                byte[] codewords = new byte[IMBE_CODEWORD_SAMPLES];
                // Encode
                encoder.encodeBits(bits, codewords);
                // Copy
                codeword = new byte[IMBE_CODEWORD_SAMPLES];
                Array.Copy(codewords, codeword, IMBE_CODEWORD_SAMPLES);
            }
        }
    }
}
