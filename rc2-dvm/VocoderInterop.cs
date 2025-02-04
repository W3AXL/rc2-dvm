using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using Serilog;

namespace rc2_dvm
{

    /// <summary>
    /// Wrapper class for the c++ dvmvocoder encoder library
    /// </summary>
    /// Using info from https://stackoverflow.com/a/315064/1842613
    public class MBEEncoder
    {
        public enum MBE_ENCODER_MODE : int
        {
            ENCODE_DMR_AMBE = 0,    //! DMR AMBE
            ENCODE_88BIT_IMBE = 1,  //! 88-bit IMBE (P25)
        };

        /// <summary>
        /// Create a new MBEEncoder
        /// </summary>
        /// <returns></returns>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr MBEEncoder_Create(MBE_ENCODER_MODE mode);

        /// <summary>
        /// Encode PCM16 samples to MBE codeword
        /// </summary>
        /// <param name="samples">Input PCM samples</param>
        /// <param name="codeword">Output MBE codeword</param>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        public static extern void MBEEncoder_Encode(IntPtr pEncoder, Int16[] samples, byte[] codeword);

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
        public MBEEncoder(MBE_ENCODER_MODE mode)
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
        public void encode(Int16[] samples, ref byte[] codeword)
        {
            MBEEncoder_Encode(encoder, samples, codeword);
        }
    }

    /// <summary>
    /// Wrapper class for the c++ dvmvocoder decoder library
    /// </summary>
    public class MBEDecoder
    {
        public enum MBE_DECODER_MODE : int
        {
            DECODE_DMR_AMBE = 0,    //! DMR AMBE
            DECODE_88BIT_IMBE = 1   //! 88-bit IMBE (P25)
        };

        /// <summary>
        /// Create a new MBEDecoder
        /// </summary>
        /// <returns></returns>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr MBEDecoder_Create(MBE_DECODER_MODE mode);

        /// <summary>
        /// Encode PCM16 samples to MBE codeword
        /// </summary>
        /// <param name="samples">Input PCM samples</param>
        /// <param name="codeword">Output MBE codeword</param>
        /// <returns>Number of decode errors</returns>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        public static extern Int32 MBEDecoder_Decode(IntPtr pDecoder, byte[] codeword, Int16[] samples);

        /// <summary>
        /// Delete a created MBEEncoder
        /// </summary>
        /// <param name="pDecoder"></param>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        public static extern void MBEDecoder_Delete(IntPtr pDecoder);

        /// <summary>
        /// Pointer to the encoder instance
        /// </summary>
        private IntPtr decoder { get; set; }

        /// <summary>
        /// Create a new MBEEncoder instance
        /// </summary>
        /// <param name="mode">Vocoder Mode (DMR or P25)</param>
        public MBEDecoder(MBE_DECODER_MODE mode)
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
        /// Encode PCM16 samples to MBE codeword
        /// </summary>
        /// <param name="samples"></param>
        /// <param name="codeword"></param>
        public Int32 decode(byte[] codeword, ref Int16[] samples)
        {
            return MBEDecoder_Decode(decoder, codeword, samples);
        }
    }
}
