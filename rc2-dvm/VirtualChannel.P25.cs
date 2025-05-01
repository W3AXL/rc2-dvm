using fnecore.P25;
using fnecore;
using NAudio.Wave;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NAudio.Dsp;
using NWaves;
using System.ComponentModel;
using NWaves.Filters;
using NWaves.Filters.OnePole;
using NAudio.Wave.SampleProviders;
using NWaves.Signals;

namespace rc2_dvm
{
    public partial class VirtualChannel
    {
        private byte[] netLDU1 = new byte[9 * 25];
        private byte[] netLDU2 = new byte[9 * 25];
        private uint p25SeqNo = 0;
        private byte p25N = 0;

        private bool ignoreCall = false;
        
        // Encryption params
        private byte callAlgoId = P25Defines.P25_ALGO_UNENCRYPT;
        private ushort callKeyId = 0;

        // Crypto handler
        P25Crypto crypto = new P25Crypto();

        private byte[] callMi = new byte[P25Defines.P25_MI_LENGTH];

        private static short[] silence = new short[FneSystemBase.MBE_SAMPLES_LENGTH];

        /// <summary>
        /// Helper to send a P25 terminator message
        /// </summary>
        /// <param name="srcId"></param>
        /// <param name="dstId"></param>
        /// <param name="grantDemand"></param>
        private void SendP25TDU(uint srcId, uint dstId, bool grantDemand = false)
        {
            RC2DVM.fneSystem.SendP25TDU(srcId, dstId, grantDemand);

            p25SeqNo = 0;
            p25N = 0;
        }

        /// <summary>
        /// Helper to encode and transmit PCM audio as P25 IMBE frames.
        /// </summary>
        /// <param name="pcm16"></param>
        /// <param name="forcedSrcId"></param>
        /// <param name="forcedDstId"></param>
        private void P25EncodeAudioFrame(short[] pcm16, uint srcId, uint dstId)
        {
            // Ensure samples are right length
            if (pcm16.Length != FneSystemBase.MBE_SAMPLES_LENGTH)
            {
                throw new ArgumentException("Input samples not proper length for MBE encoding!");
            }

            if (p25N > 17)
                p25N = 0;
            if (p25N == 0)
                FneUtils.Memset(netLDU1, 0, 9 * 25);
            if (p25N == 9)
                FneUtils.Memset(netLDU2, 0, 9 * 25);

            // Log.Logger.Debug($"BYTE BUFFER {FneUtils.HexDump(pcm)}");

            // Convert to floats
            float[] fSamples = Utils.PcmToFloat(pcm16);

            // Convert to signal
            DiscreteSignal signal = new DiscreteSignal(waveFormat.SampleRate, fSamples, true);

            // buffer for IMBE codeword
            byte[] imbe = new byte[FneSystemBase.IMBE_BUF_LEN];

            // Detect tone and send tone codeword if detected
            int tone = 0;
            if (Config.AudioConfig.TxToneDetection)
            {
                tone = toneDetector.Detect(signal);
            }
            if (tone > 0)
            {
                MBEToneGenerator.IMBEEncodeSingleTone((ushort)tone, imbe);
                Log.Logger.Debug($"({Config.Name}) P25D: {tone} HZ TONE DETECT");
            }
            else
            {
                // Apply filter
                DiscreteSignal filtered = txAudioFilter.ApplyTo(signal);

                // Apply Gain
                filtered = filtered * Config.AudioConfig.TxAudioGain;

                // Convert back to pcm16 samples
                short[] filtered16 = Utils.FloatToPcm(filtered.Samples);

                // TX local repeat
                if (Config.AudioConfig.TxLocalRepeat)
                {
                    byte[] pcm = new byte[filtered16.Length * 2];
                    Buffer.BlockCopy(filtered16, 0, pcm, 0, pcm.Length);
                    bufferedWaveProvider.AddSamples(pcm, 0, pcm.Length);
                }

                //Log.Logger.Debug($"SAMPLE BUFFER {FneUtils.HexDump(filtered16)}");

                // encode PCM samples into IMBE codewords
#if WIN32
                if (extFullRateVocoder != null)
                    extFullRateVocoder.encode(filtered16, out imbe);
                else
                    encoder.encode(filtered16, imbe);
#else
                encoder.encode(filtered16, imbe);
#endif
                //Log.Logger.Debug($"IMBE {FneUtils.HexDump(imbe)}");
            }

#if ENCODER_LOOPBACK_TEST
            short[] samp2 = null;
            int errs = p25Decoder.decode(imbe, out samp2);
            if (samples != null)
            {
                Log.Logger.Debug($"LOOPBACK_TEST IMBE {FneUtils.HexDump(imbe)}");
                Log.Logger.Debug($"LOOPBACK_TEST SAMPLE BUFFER {FneUtils.HexDump(samp2)}");

                int pcmIdx = 0;
                byte[] pcm2 = new byte[samp2.Length * 2];
                for (int smpIdx2 = 0; smpIdx2 < samp2.Length; smpIdx2++)
                {
                    pcm2[pcmIdx + 0] = (byte)(samp2[smpIdx2] & 0xFF);
                    pcm2[pcmIdx + 1] = (byte)((samp2[smpIdx2] >> 8) & 0xFF);
                    pcmIdx += 2;
                }

                Log.Logger.Debug($"LOOPBACK_TEST BYTE BUFFER {FneUtils.HexDump(pcm2)}");
                waveProvider.AddSamples(pcm2, 0, pcm2.Length);
            }
#else
            // Encrypt call if encryption selected or strapped
            if (CurrentTalkgroup.Strapped || Secure)
            {

                // KEYFAIL if keys aren't loaded
                if (!loadedKeys.ContainsKey(CurrentTalkgroup.KeyId))
                {
                    Log.Logger.Error("({0:l}) KEYFAIL: {TG} ({TGID}) configured for missing Key ID {id}", Config.Name, CurrentTalkgroup.Name, CurrentTalkgroup.DestinationId, CurrentTalkgroup.KeyId);
                    return;
                }

                // Set up initial MI
                if (p25N == 0)
                {
                    if (callMi.All(b => b == 0))
                    {
                        Random random = new Random();

                        for (int i = 0; i < P25Defines.P25_MI_LENGTH; i++)
                        {
                            callMi[i] = (byte)random.Next(0x00, 0x100);
                        }
                    }
                    if (!crypto.Prepare(CurrentTalkgroup.AlgId, CurrentTalkgroup.KeyId, callMi))
                    {
                        Log.Logger.Error("({0:l}) Failed to prepare crypto params for Key {keyId}", Config.Name, CurrentTalkgroup.KeyId);
                        return;
                    }
                    //Log.Logger.Debug("({0:l}) Prepared initial crypto params for TX", Config.Name);
                }

                // Encrypt
                if (!crypto.Process(imbe, p25N < 9U ? P25DUID.LDU1 : P25DUID.LDU2))
                {
                    Log.Logger.Error("({0:l}) Failed to encrypt call data!");
                    return;
                }

                // Prepare a new MI on last block of LDU2
                if (p25N == 17U)
                {
                    P25Crypto.CycleP25Lfsr(callMi);
                    if (!crypto.Prepare(CurrentTalkgroup.AlgId, CurrentTalkgroup.KeyId, callMi))
                    {
                        Log.Logger.Error("({0:l}) Failed to prepare crypto params for Key {keyId}", Config.Name, CurrentTalkgroup.KeyId);
                        return;
                    }
                }

                //Log.Logger.Debug("({0:l}) TG {tg} ({TGID}) is strapped or switched secure", Config.Name, CurrentTalkgroup.Name, CurrentTalkgroup.DestinationId);
            }
            
            // fill the LDU buffers appropriately
            switch (p25N)
            {
                // LDU1
                case 0:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 10, FneSystemBase.IMBE_BUF_LEN);
                    break;
                case 1:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 26, FneSystemBase.IMBE_BUF_LEN);
                    break;
                case 2:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 55, FneSystemBase.IMBE_BUF_LEN);
                    break;
                case 3:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 80, FneSystemBase.IMBE_BUF_LEN);
                    break;
                case 4:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 105, FneSystemBase.IMBE_BUF_LEN);
                    break;
                case 5:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 130, FneSystemBase.IMBE_BUF_LEN);
                    break;
                case 6:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 155, FneSystemBase.IMBE_BUF_LEN);
                    break;
                case 7:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 180, FneSystemBase.IMBE_BUF_LEN);
                    break;
                case 8:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 204, FneSystemBase.IMBE_BUF_LEN);
                    break;

                // LDU2
                case 9:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 10, FneSystemBase.IMBE_BUF_LEN);
                    break;
                case 10:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 26, FneSystemBase.IMBE_BUF_LEN);
                    break;
                case 11:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 55, FneSystemBase.IMBE_BUF_LEN);
                    break;
                case 12:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 80, FneSystemBase.IMBE_BUF_LEN);
                    break;
                case 13:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 105, FneSystemBase.IMBE_BUF_LEN);
                    break;
                case 14:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 130, FneSystemBase.IMBE_BUF_LEN);
                    break;
                case 15:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 155, FneSystemBase.IMBE_BUF_LEN);
                    break;
                case 16:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 180, FneSystemBase.IMBE_BUF_LEN);
                    break;
                case 17:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 204, FneSystemBase.IMBE_BUF_LEN);
                    break;
            }

            FnePeer peer = RC2DVM.fneSystem.peer;
            RemoteCallData callData = new RemoteCallData()
            {
                SrcId = srcId,
                DstId = dstId,
                LCO = P25Defines.LC_GROUP
            };

            // Setup crypto params
            CryptoParams cryptoParams = new CryptoParams();
            if (CurrentTalkgroup.AlgId != P25Defines.P25_ALGO_UNENCRYPT && CurrentTalkgroup.KeyId > 0 && (CurrentTalkgroup.Strapped || Secure))
            {
                cryptoParams.AlgoId = CurrentTalkgroup.AlgId;
                cryptoParams.KeyId = CurrentTalkgroup.KeyId;
                Array.Copy(callMi, cryptoParams.MI, P25Defines.P25_MI_LENGTH);
            }

            // send P25 LDU1
            if (p25N == 8U)
            {
                ushort pktSeq = 0;
                if (p25SeqNo == 0U)
                    pktSeq = peer.pktSeq(true);
                else
                    pktSeq = peer.pktSeq();

                Log.Logger.Information("({0:l}) P25D: Traffic *VOICE FRAME LDU1* PEER {1} SRC_ID {2} TGID {3} [STREAM ID {4}]", Config.Name, RC2DVM.fneSystem.PeerId, srcId, dstId, txStreamId);

                byte[] payload = new byte[200];
                RC2DVM.fneSystem.CreateP25MessageHdr((byte)P25DUID.LDU1, callData, ref payload, cryptoParams);
                RC2DVM.fneSystem.CreateP25LDU1Message(netLDU1, ref payload, srcId, dstId);

                peer.SendMaster(new Tuple<byte, byte>(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_P25), payload, pktSeq, txStreamId);
            }

            // send P25 LDU2
            if (p25N == 17U)
            {
                ushort pktSeq = 0;
                if (p25SeqNo == 0U)
                    pktSeq = peer.pktSeq(true);
                else
                    pktSeq = peer.pktSeq();

                Log.Logger.Information("({0:l}) P25D: Traffic *VOICE FRAME LDU2* PEER {1} SRC_ID {2} TGID {3} [STREAM ID {4}]", Config.Name, RC2DVM.fneSystem.PeerId, srcId, dstId, txStreamId);

                byte[] payload = new byte[200];
                RC2DVM.fneSystem.CreateP25MessageHdr((byte)P25DUID.LDU2, callData, ref payload, cryptoParams);
                RC2DVM.fneSystem.CreateP25LDU2Message(netLDU2, ref payload, cryptoParams);

                peer.SendMaster(new Tuple<byte, byte>(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_P25), payload, pktSeq, txStreamId);
            }

            p25SeqNo++;
            p25N++;
#endif
        }

        /// <summary>
        /// Helper to decode and playback P25 IMBE frames as PCM audio.
        /// </summary>
        /// <param name="ldu"></param>
        /// <param name="e"></param>
        private void P25DecodeAudioFrame(byte[] ldu, P25DataReceivedEvent e, P25DUID duid = P25DUID.LDU1)
        {
            // We've received an LDU so reset the rx data timer
            rxDataTimer.Stop();
            rxDataTimer.Start();

            // Try to deccode audio
            try
            {
                // decode 9 IMBE codewords into PCM samples
                for (int n = 0; n < 9; n++)
                {
                    byte[] imbe = new byte[FneSystemBase.IMBE_BUF_LEN];
                    switch (n)
                    {
                        case 0:
                            Buffer.BlockCopy(ldu, 10, imbe, 0, FneSystemBase.IMBE_BUF_LEN);
                            break;
                        case 1:
                            Buffer.BlockCopy(ldu, 26, imbe, 0, FneSystemBase.IMBE_BUF_LEN);
                            break;
                        case 2:
                            Buffer.BlockCopy(ldu, 55, imbe, 0, FneSystemBase.IMBE_BUF_LEN);
                            break;
                        case 3:
                            Buffer.BlockCopy(ldu, 80, imbe, 0, FneSystemBase.IMBE_BUF_LEN);
                            break;
                        case 4:
                            Buffer.BlockCopy(ldu, 105, imbe, 0, FneSystemBase.IMBE_BUF_LEN);
                            break;
                        case 5:
                            Buffer.BlockCopy(ldu, 130, imbe, 0, FneSystemBase.IMBE_BUF_LEN);
                            break;
                        case 6:
                            Buffer.BlockCopy(ldu, 155, imbe, 0, FneSystemBase.IMBE_BUF_LEN);
                            break;
                        case 7:
                            Buffer.BlockCopy(ldu, 180, imbe, 0, FneSystemBase.IMBE_BUF_LEN);
                            break;
                        case 8:
                            Buffer.BlockCopy(ldu, 204, imbe, 0, FneSystemBase.IMBE_BUF_LEN);
                            break;
                    }

                    //Log.Logger.Debug($"Decoding IMBE buffer: {FneUtils.HexDump(imbe)}");

                    short[] samples = new short[FneSystemBase.MBE_SAMPLES_LENGTH];

                    // Run through crypter
                    crypto.Process(imbe, duid);

                    int errs = 0;
#if WIN32
                    if (extFullRateVocoder != null)
                        errs = extFullRateVocoder.decode(imbe, out samples);
                    else
                        errs = decoder.decode(imbe, samples);
#else
                    errs = decoder.decode(imbe, samples);
#endif
                    if (samples != null)
                    {
                        //Log.Logger.Debug($"({Config.Name}) P25D: Traffic *VOICE FRAME    * PEER {e.PeerId} SRC_ID {e.SrcId} TGID {e.DstId} VC{n} ERRS {errs} [STREAM ID {e.StreamId}]");
                        //Log.Logger.Debug($"IMBE {FneUtils.HexDump(imbe)}");
                        //Log.Logger.Debug($"SAMPLE BUFFER {FneUtils.HexDump(samples)}");

                        if (errs != 0)
                        {
                            Log.Logger.Warning("({0:l}) P25D: Decode Errors: {errs}", errs);
                        }

                        // Convert to floats
                        float[] fSamples = Utils.PcmToFloat(samples);

                        // Apply filter
                        DiscreteSignal signal = new DiscreteSignal(waveFormat.SampleRate, fSamples, true);
                        DiscreteSignal filtered = rxAudioFilter.ApplyTo(signal);

                        // Apply Gain
                        filtered = filtered * Config.AudioConfig.RxAudioGain;

                        // Convert back to pcm16 samples
                        short[] filtered16 = Utils.FloatToPcm(filtered.Samples);

                        // Send to WebRTC
                        dvmRadio.RxSendPCM16Samples(filtered16, FneSystemBase.SAMPLE_RATE);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Error($"Audio Decode Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Event handler used to process incoming P25 data.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public void P25DataReceived(P25DataReceivedEvent e, DateTime pktTime)
        {

            uint sysId = (uint)((e.Data[11U] << 8) | (e.Data[12U] << 0));
            uint netId = FneUtils.Bytes3ToUInt32(e.Data, 16);
            byte control = e.Data[14U];

            byte len = e.Data[23];
            byte[] data = new byte[len];
            for (int i = 24; i < len; i++)
                data[i - 24] = e.Data[i];

            // if this is an LDU1 see if this is the first LDU with HDU encryption data
            if (e.DUID == P25DUID.LDU1 && !ignoreCall)
            {
                byte frameType = e.Data[180];
                if (frameType == P25Defines.P25_FT_HDU_VALID)
                {
                    // Get Alg & KID
                    callAlgoId = e.Data[181];
                    callKeyId = (ushort)(e.Data[182] << 8 | e.Data[183]);
                    // Copy MI
                    Array.Copy(e.Data, 184, callMi, 0, P25Defines.P25_MI_LENGTH);
                    
                    // Validate key
                    if (Config.StrictKeyMapping && callKeyId != CurrentTalkgroup.KeyId)
                    {
                        Log.Logger.Warning("({0:l}) P25D: Ignoring traffic for non-matching key ID 0x{keyID:X4}", Config.Name, callKeyId);
                        ignoreCall = true;
                    }
                    else if (!loadedKeys.ContainsKey(callKeyId))
                    {
                        Log.Logger.Warning("({0:l}) P25D: Ignoring traffic for missing key ID 0x{keyID:X4}", Config.Name, callKeyId);
                        ignoreCall = true;
                    }
                    else
                    {
                        // Set Key
                        crypto.SetKey(callKeyId, callAlgoId, loadedKeys[callKeyId].GetKey());
                        // Set up crypto engine
                        crypto.Prepare(callAlgoId, callKeyId, callMi);
                        // Log
                        Log.Logger.Debug("({0:l}) Preparing decryption for Key ID {keyID:X4} ({algo:l})", Config.Name, CurrentTalkgroup.KeyId, Enum.GetName(typeof(Algorithm), CurrentTalkgroup.AlgId));
                    }
                    
                }
            }

            // is this a new call stream?
            if (e.StreamId != status[FneSystemBase.P25_FIXED_SLOT].RxStreamId && ((e.DUID != P25DUID.TDU) && (e.DUID != P25DUID.TDULC)))
            {
                callInProgress = true;
                status[FneSystemBase.P25_FIXED_SLOT].RxStart = pktTime;
                
                // Clear MI
                FneUtils.Memset(callMi, 0x00, P25Defines.P25_MI_LENGTH);
                
                // Fix incorrect algo/key IDs
                if (callAlgoId == 0 && callKeyId == 0)
                {
                    callAlgoId = P25Defines.P25_ALGO_UNENCRYPT;
                }
                
                // Update state depending on ecryption status
                if (callAlgoId != P25Defines.P25_ALGO_UNENCRYPT)
                    dvmRadio.Status.State = rc2_core.RadioState.Encrypted;
                else
                    dvmRadio.Status.State = rc2_core.RadioState.Receiving;
                
                // Start source ID display callback
                lastSourceId = e.SrcId;
                sourceIdTimer.Start();
                // Start RX data timeout timer
                rxDataTimer.Start();
                // Status update
                dvmRadio.StatusCallback();
                
                // Log
                if (callAlgoId != P25Defines.P25_ALGO_UNENCRYPT)
                {
                    Log.Logger.Information("({0:l}) P25D: Traffic *ENC CALL START* PEER {1} SRC_ID {2} TGID {3} ALGO {4:l} KEY 0x{5:X4} [STREAM ID {6}]", Config.Name, e.PeerId, e.SrcId, e.DstId, Enum.GetName(typeof(Algorithm), callAlgoId), callKeyId, e.StreamId);
                }
                else
                {
                    Log.Logger.Information("({0:l}) P25D: Traffic *CALL START    * PEER {1} SRC_ID {2} TGID {3} [STREAM ID {4}]", Config.Name, e.PeerId, e.SrcId, e.DstId, e.StreamId);
                }
                
            }

            // Is the call over?
            if (((e.DUID == P25DUID.TDU) || (e.DUID == P25DUID.TDULC)) && (status[FneSystemBase.P25_FIXED_SLOT].RxType != FrameType.TERMINATOR))
            {
                // Reset flags
                ignoreCall = false;
                callInProgress = false;
                callAlgoId = P25Defines.P25_ALGO_UNENCRYPT;
                TimeSpan callDuration = pktTime - status[FneSystemBase.P25_FIXED_SLOT].RxStart;
                // Update state
                dvmRadio.Status.State = rc2_core.RadioState.Idle;
                // Stop source ID callback
                sourceIdTimer.Stop();
                dvmRadio.Status.ChannelName = CurrentTalkgroup.Name;
                // Stop RX data timeout timer
                rxDataTimer.Stop();
                dvmRadio.Status.CallerId = "";
                // Status update
                dvmRadio.StatusCallback();
                // Log
                Log.Logger.Information($"({Config.Name}) P25D: Traffic *CALL END       * PEER {e.PeerId} SRC_ID {e.SrcId} TGID {e.DstId} DUR {callDuration} [STREAM ID {e.StreamId}]");
                return;
            }

            if (ignoreCall && callAlgoId == P25Defines.P25_ALGO_UNENCRYPT)
                ignoreCall = false;

            if (e.DUID == P25DUID.LDU2 && !ignoreCall)
                callAlgoId = data[88];

            if (ignoreCall)
                return;

            /*if (callAlgoId != P25Defines.P25_ALGO_UNENCRYPT)
            {
                if (status[FneSystemBase.P25_FIXED_SLOT].RxType != FrameType.TERMINATOR)
                {
                    callInProgress = false;
                    TimeSpan callDuration = pktTime - status[FneSystemBase.P25_FIXED_SLOT].RxStart;
                    Log.Logger.Information($"({Config.Name}) P25D: Traffic *CALL END (T)    * PEER {e.PeerId} SRC_ID {e.SrcId} TGID {e.DstId} DUR {callDuration} [STREAM ID {e.StreamId}]");
                }

                // Send an extra block of silent PCM samples to prevent the weird artifacting at the end of calls
                dvmRadio.RxSendPCM16Samples(silence, FneSystemBase.SAMPLE_RATE);

                ignoreCall = true;
                return;
            }*/

            // At this point we chan check for late entry
            if (!ignoreCall && !callInProgress)
            {
                callInProgress = true;
                //callAlgoId = P25Defines.P25_ALGO_UNENCRYPT;
                status[FneSystemBase.P25_FIXED_SLOT].RxStart = pktTime;
                // Update status
                dvmRadio.Status.State = rc2_core.RadioState.Receiving;
                dvmRadio.StatusCallback();
                // Log
                if (callAlgoId != P25Defines.P25_ALGO_UNENCRYPT)
                {
                    Log.Logger.Information("({0}) P25D: Traffic *ENC CALL LATE START* PEER {1} SRC_ID {2} TGID {3} ALGO {4:l} KEY 0x{5:X4} [STREAM ID {6}]", Config.Name, e.PeerId, e.SrcId, e.DstId, Enum.GetName(typeof(Algorithm), callAlgoId), callKeyId, e.StreamId);
                }
                else
                {
                    Log.Logger.Information("({0}) P25D: Traffic *CALL LATE START    * PEER {1} SRC_ID {2} TGID {3} [STREAM ID {4}]", Config.Name, e.PeerId, e.SrcId, e.DstId, e.StreamId);
                }
            }

            // New MI data
            byte[] newMI = new byte[P25Defines.P25_MI_LENGTH];

            int count = 0;
            switch (e.DUID)
            {
                case P25DUID.LDU1:
                    {
                        // The '62', '63', '64', '65', '66', '67', '68', '69', '6A' records are LDU1
                        if ((data[0U] == 0x62U) && (data[22U] == 0x63U) &&
                            (data[36U] == 0x64U) && (data[53U] == 0x65U) &&
                            (data[70U] == 0x66U) && (data[87U] == 0x67U) &&
                            (data[104U] == 0x68U) && (data[121U] == 0x69U) &&
                            (data[138U] == 0x6AU))
                        {
                            // The '62' record - IMBE Voice 1
                            Buffer.BlockCopy(data, count, netLDU1, 0, 22);
                            count += 22;

                            // The '63' record - IMBE Voice 2
                            Buffer.BlockCopy(data, count, netLDU1, 25, 14);
                            count += 14;

                            // The '64' record - IMBE Voice 3 + Link Control
                            Buffer.BlockCopy(data, count, netLDU1, 50, 17);
                            count += 17;

                            // The '65' record - IMBE Voice 4 + Link Control
                            Buffer.BlockCopy(data, count, netLDU1, 75, 17);
                            count += 17;

                            // The '66' record - IMBE Voice 5 + Link Control
                            Buffer.BlockCopy(data, count, netLDU1, 100, 17);
                            count += 17;

                            // The '67' record - IMBE Voice 6 + Link Control
                            Buffer.BlockCopy(data, count, netLDU1, 125, 17);
                            count += 17;

                            // The '68' record - IMBE Voice 7 + Link Control
                            Buffer.BlockCopy(data, count, netLDU1, 150, 17);
                            count += 17;

                            // The '69' record - IMBE Voice 8 + Link Control
                            Buffer.BlockCopy(data, count, netLDU1, 175, 17);
                            count += 17;

                            // The '6A' record - IMBE Voice 9 + Low Speed Data
                            Buffer.BlockCopy(data, count, netLDU1, 200, 16);
                            count += 16;

                            // decode 9 IMBE codewords into PCM samples
                            P25DecodeAudioFrame(netLDU1, e, P25DUID.LDU1);
                        }
                    }
                    break;
                case P25DUID.LDU2:
                    {
                        // The '6B', '6C', '6D', '6E', '6F', '70', '71', '72', '73' records are LDU2
                        if ((data[0U] == 0x6BU) && (data[22U] == 0x6CU) &&
                            (data[36U] == 0x6DU) && (data[53U] == 0x6EU) &&
                            (data[70U] == 0x6FU) && (data[87U] == 0x70U) &&
                            (data[104U] == 0x71U) && (data[121U] == 0x72U) &&
                            (data[138U] == 0x73U))
                        {
                            // The '6B' record - IMBE Voice 10
                            Buffer.BlockCopy(data, count, netLDU2, 0, 22);
                            count += 22;

                            // The '6C' record - IMBE Voice 11
                            Buffer.BlockCopy(data, count, netLDU2, 25, 14);
                            count += 14;

                            // The '6D' record - IMBE Voice 12 + Encryption Sync
                            Buffer.BlockCopy(data, count, netLDU2, 50, 17);
                            newMI[0] = data[count + 1];
                            newMI[1] = data[count + 2];
                            newMI[2] = data[count + 3];
                            count += 17;

                            // The '6E' record - IMBE Voice 13 + Encryption Sync
                            Buffer.BlockCopy(data, count, netLDU2, 75, 17);
                            newMI[3] = data[count + 1];
                            newMI[4] = data[count + 2];
                            newMI[5] = data[count + 3];
                            count += 17;

                            // The '6F' record - IMBE Voice 14 + Encryption Sync
                            Buffer.BlockCopy(data, count, netLDU2, 100, 17);
                            newMI[6] = data[count + 1];
                            newMI[7] = data[count + 2];
                            newMI[8] = data[count + 3];
                            count += 17;

                            // The '70' record - IMBE Voice 15 + Encryption Sync
                            Buffer.BlockCopy(data, count, netLDU2, 125, 17);
                            callAlgoId = data[count + 1];
                            callKeyId = (ushort)((data[count + 2] << 8) | data[count + 3]);
                            count += 17;

                            // The '71' record - IMBE Voice 16 + Encryption Sync
                            Buffer.BlockCopy(data, count, netLDU2, 150, 17);
                            count += 17;

                            // The '72' record - IMBE Voice 17 + Encryption Sync
                            Buffer.BlockCopy(data, count, netLDU2, 175, 17);
                            count += 17;

                            // The '73' record - IMBE Voice 18 + Low Speed Data
                            Buffer.BlockCopy(data, count, netLDU2, 200, 16);
                            count += 16;

                            // TODO: Actually detect errors and use LFSR instead of just copying the MI
                            Array.Copy(newMI, callMi, P25Defines.P25_MI_LENGTH);

                            // decode 9 IMBE codewords into PCM samples
                            P25DecodeAudioFrame(netLDU2, e, P25DUID.LDU2);
                        }
                    }
                    break;
            }

            // Prepare the crypto processor
            if (callMi != null)
                crypto.Prepare(callAlgoId, callKeyId, callMi);

            status[FneSystemBase.P25_FIXED_SLOT].RxRFS = e.SrcId;
            status[FneSystemBase.P25_FIXED_SLOT].RxType = e.FrameType;
            status[FneSystemBase.P25_FIXED_SLOT].RxTGId = e.DstId;
            status[FneSystemBase.P25_FIXED_SLOT].RxTime = pktTime;
            status[FneSystemBase.P25_FIXED_SLOT].RxStreamId = e.StreamId;

            return;
        }
    }
}
