using fnecore.DMR;
using fnecore;
using NAudio.Wave;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace rc2_dvm
{
    public partial class VirtualChannel
    {
        private EmbeddedData embeddedData = new EmbeddedData();

        private byte[] ambeBuffer = new byte[27];
        private int ambeCount = 0;
        private int dmrSeqNo = 0;
        private byte dmrN = 0;

        /// <summary>
        /// Pass DMR frame information to the FNE system for creation and transmission
        /// </summary>
        /// <param name="data"></param>
        /// <param name="srcId"></param>
        /// <param name="dstId"></param>
        /// <param name="slot"></param>
        /// <param name="frameType"></param>
        /// <param name="seqNo"></param>
        /// <param name="n"></param>
        private void CreateDMRMessage(ref byte[] data, uint srcId, uint dstId, byte slot, FrameType frameType, byte seqNo, byte n)
        {
            RC2DVM.fneSystem.CreateDMRMessage(ref data, srcId, dstId, slot, frameType, seqNo, n);
        }

        /// <summary>
        /// Helper to send a DMR terminator with LC message.
        /// </summary>
        private void SendDMRTerminator(uint srcId, uint dstId, byte slot)
        {
            RC2DVM.fneSystem.SendDMRTerminator(srcId, dstId, slot, dmrSeqNo, dmrN, embeddedData);
            ambeCount = 0;
        }

        /// <summary>
        /// Helper to encode and transmit PCM audio as DMR AMBE frames.
        /// </summary>
        /// <param name="srcId">Source ID</param>
        /// <param name="dstId">Destination ID</param>
        /// <param name="pcm">PCM audio data</param>
        private void DMREncodeAudioFrame(uint srcId, uint dstId, byte slot, byte[] pcm, float txGain = 1.0f)
        {
#if ENCODER_LOOPBACK_TEST
            if (ambeCount == AMBE_PER_SLOT)
            {
                for (int n = 0; n < AMBE_PER_SLOT; n++)
                {
                    byte[] ambePartial = new byte[AMBE_BUF_LEN];
                    for (int i = 0; i < AMBE_BUF_LEN; i++)
                        ambePartial[i] = ambeBuffer[i + (n * 9)];

                    short[] samp = null;
                    int errs = dmrDecoder.decode(ambePartial, out samp);
                    if (samp != null)
                    {
                        Log.Logger.Debug($"LOOPBACK_TEST PARTIAL AMBE {FneUtils.HexDump(ambePartial)}");
                        Log.Logger.Debug($"LOOPBACK_TEST SAMPLE BUFFER {FneUtils.HexDump(samp)}");

                        int pcmIdx = 0;
                        byte[] pcm2 = new byte[samp.Length * 2];
                        for (int smpIdx2 = 0; smpIdx2 < samp.Length; smpIdx2++)
                        {
                            pcm2[pcmIdx + 0] = (byte)(samp[smpIdx2] & 0xFF);
                            pcm2[pcmIdx + 1] = (byte)((samp[smpIdx2] >> 8) & 0xFF);
                            pcmIdx += 2;
                        }

                        Log.Logger.Debug($"LOOPBACK_TEST BYTE BUFFER {FneUtils.HexDump(pcm)}");
                        waveProvider.AddSamples(pcm2, 0, pcm2.Length);
                    }
                }

                FneUtils.Memset(ambeBuffer, 0, 27);
                ambeCount = 0;
            }
#else
            byte[] data = null, dmrpkt = null;
            dmrN = (byte)(dmrSeqNo % 6);
            if (ambeCount == FneSystemBase.AMBE_PER_SLOT)
            {
                FnePeer peer = RC2DVM.fneSystem.peer;
                ushort pktSeq = 0;

                // is this the intitial sequence?
                if (dmrSeqNo == 0)
                {
                    pktSeq = peer.pktSeq(true);

                    // send DMR voice header
                    data = new byte[FneSystemBase.DMR_FRAME_LENGTH_BYTES];

                    // generate DMR LC
                    LC dmrLC = new LC();
                    dmrLC.FLCO = (byte)DMRFLCO.FLCO_GROUP;
                    dmrLC.SrcId = srcId;
                    dmrLC.DstId = dstId;
                    embeddedData.SetLC(dmrLC);

                    // generate the Slot TYpe
                    SlotType slotType = new SlotType();
                    slotType.DataType = (byte)DMRDataType.VOICE_LC_HEADER;
                    slotType.GetData(ref data);

                    FullLC.Encode(dmrLC, ref data, DMRDataType.VOICE_LC_HEADER);

                    // generate DMR network frame
                    dmrpkt = new byte[FneSystemBase.DMR_PACKET_SIZE];
                    CreateDMRMessage(ref dmrpkt, srcId, dstId, slot, FrameType.VOICE_SYNC, (byte)dmrSeqNo, 0);
                    Buffer.BlockCopy(data, 0, dmrpkt, 20, FneSystemBase.DMR_FRAME_LENGTH_BYTES);

                    peer.SendMaster(new Tuple<byte, byte>(fnecore.Constants.NET_FUNC_PROTOCOL, fnecore.Constants.NET_PROTOCOL_SUBFUNC_DMR), dmrpkt, pktSeq, txStreamId);

                    dmrSeqNo++;
                }

                pktSeq = peer.pktSeq();

                // send DMR voice
                data = new byte[FneSystemBase.DMR_FRAME_LENGTH_BYTES];

                Buffer.BlockCopy(ambeBuffer, 0, data, 0, 13);
                data[13U] = (byte)(ambeBuffer[13U] & 0xF0);
                data[19U] = (byte)(ambeBuffer[13U] & 0x0F);
                Buffer.BlockCopy(ambeBuffer, 14, data, 20, 13);

                FrameType frameType = FrameType.VOICE_SYNC;
                if (dmrN == 0)
                    frameType = FrameType.VOICE_SYNC;
                else
                {
                    frameType = FrameType.VOICE;

                    byte lcss = embeddedData.GetData(ref data, dmrN);

                    // generated embedded signalling
                    EMB emb = new EMB();
                    emb.ColorCode = 0;
                    emb.LCSS = lcss;
                    emb.Encode(ref data);
                }

                Log.Logger.Information($"({Config.Name}) DMRD: Traffic *VOICE FRAME    * PEER {RC2DVM.fneSystem.PeerId} SRC_ID {srcId} TGID {dstId} TS {slot} VC{dmrN} [STREAM ID {txStreamId}]");

                // generate DMR network frame
                dmrpkt = new byte[FneSystemBase.DMR_PACKET_SIZE];
                CreateDMRMessage(ref dmrpkt, srcId, dstId, slot, frameType, (byte)dmrSeqNo, dmrN);
                Buffer.BlockCopy(data, 0, dmrpkt, 20, FneSystemBase.DMR_FRAME_LENGTH_BYTES);

                peer.SendMaster(new Tuple<byte, byte>(fnecore.Constants.NET_FUNC_PROTOCOL, fnecore.Constants.NET_PROTOCOL_SUBFUNC_DMR), dmrpkt, pktSeq, txStreamId);

                dmrSeqNo++;

                FneUtils.Memset(ambeBuffer, 0, 27);
                ambeCount = 0;
            }
#endif
            // Log.Logger.Debug($"BYTE BUFFER {FneUtils.HexDump(pcm)}");

            // pre-process: apply gain to PCM audio frames
            if (txGain != 1.0f)
            {
                BufferedWaveProvider buffer = new BufferedWaveProvider(waveFormat);
                buffer.AddSamples(pcm, 0, pcm.Length);

                VolumeWaveProvider16 gainControl = new VolumeWaveProvider16(buffer);
                gainControl.Volume = txGain;
                gainControl.Read(pcm, 0, pcm.Length);
            }

            int smpIdx = 0;
            short[] samples = new short[FneSystemBase.MBE_SAMPLES_LENGTH];
            for (int pcmIdx = 0; pcmIdx < pcm.Length; pcmIdx += 2)
            {
                samples[smpIdx] = (short)((pcm[pcmIdx + 1] << 8) + pcm[pcmIdx + 0]);
                smpIdx++;
            }

            // Log.Logger.Debug($"SAMPLE BUFFER {FneUtils.HexDump(samples)}");

            // encode PCM samples into AMBE codewords
            byte[] ambe = new byte[FneSystemBase.AMBE_BUF_LEN];
#if WIN32
            if (extHalfRateVocoder != null)
                extHalfRateVocoder.encode(samples, out ambe, true);
            else
                encoder.encode(samples, out ambe);
#else
            encoder.encode(in samples, out ambe);
#endif
            // Log.Logger.Debug($"AMBE {FneUtils.HexDump(ambe)}");

            Buffer.BlockCopy(ambe, 0, ambeBuffer, ambeCount * 9, FneSystemBase.AMBE_BUF_LEN);
            ambeCount++;
        }

        /// <summary>
        /// Helper to decode and playback DMR AMBE frames as PCM audio.
        /// </summary>
        /// <param name="ambe"></param>
        /// <param name="e"></param>
        private void DMRDecodeAudioFrame(byte[] ambe, DMRDataReceivedEvent e)
        {
            try
            {
                // Log.Logger.Debug($"FULL AMBE {FneUtils.HexDump(ambe)}");
                for (int n = 0; n < FneSystemBase.AMBE_PER_SLOT; n++)
                {
                    byte[] ambePartial = new byte[FneSystemBase.AMBE_BUF_LEN];
                    for (int i = 0; i < FneSystemBase.AMBE_BUF_LEN; i++)
                        ambePartial[i] = ambe[i + (n * 9)];

                    short[] samples = new short[FneSystemBase.MBE_SAMPLES_LENGTH];
                    int errs = 0;
#if WIN32
                    if (extHalfRateVocoder != null)
                        errs = extHalfRateVocoder.decode(ambePartial, out samples);
                    else
                        errs = decoder.decode(ambePartial, out samples);
#else
                    errs = decoder.decode(ambePartial, samples);
#endif

                    if (samples != null)
                    {
                        Log.Logger.Information($"({Config.Name}) DMRD: Traffic *VOICE FRAME    * PEER {e.PeerId} SRC_ID {e.SrcId} TGID {e.DstId} TS {e.Slot + 1} VC{e.n}.{n} ERRS {errs} [STREAM ID {e.StreamId}]");
                        // Log.Logger.Debug($"PARTIAL AMBE {FneUtils.HexDump(ambePartial)}");
                        // Log.Logger.Debug($"SAMPLE BUFFER {FneUtils.HexDump(samples)}");

                        int pcmIdx = 0;
                        byte[] pcm = new byte[samples.Length * 2];
                        for (int smpIdx = 0; smpIdx < samples.Length; smpIdx++)
                        {
                            pcm[pcmIdx + 0] = (byte)(samples[smpIdx] & 0xFF);
                            pcm[pcmIdx + 1] = (byte)((samples[smpIdx] >> 8) & 0xFF);
                            pcmIdx += 2;
                        }

                        // post-process: apply gain to decoded audio frames
                        if (Config.AudioConfig.RxAudioGain != 1.0f)
                        {
                            BufferedWaveProvider buffer = new BufferedWaveProvider(waveFormat);
                            buffer.AddSamples(pcm, 0, pcm.Length);

                            VolumeWaveProvider16 gainControl = new VolumeWaveProvider16(buffer);
                            gainControl.Volume = Config.AudioConfig.RxAudioGain;
                            gainControl.Read(pcm, 0, pcm.Length);
                        }

                        // TODO: Write the decoded audio to the WebRTC audio stream

                    }
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Error($"Audio Decode Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle received DMR data from the network
        /// </summary>
        public void DMRHandleRecieved(DMRDataReceivedEvent e, byte[] data, DateTime pktTime)
        {
            // is this a new call stream?
            if (e.StreamId != status[e.Slot].RxStreamId)
            {
                callInProgress = true;
                status[e.Slot].RxStart = pktTime;
                Log.Logger.Information($"({Config.Name}) DMRD: Traffic *CALL START     * PEER {e.PeerId} SRC_ID {e.SrcId} TGID {e.DstId} [STREAM ID {e.StreamId}]");

                // if we can, use the LC from the voice header as to keep all options intact
                if ((e.FrameType == FrameType.DATA_SYNC) && (e.DataType == DMRDataType.VOICE_LC_HEADER))
                {
                    LC lc = FullLC.Decode(data, DMRDataType.VOICE_LC_HEADER);
                    status[e.Slot].DMR_RxLC = lc;
                }
                else // if we don't have a voice header; don't wait to decode it, just make a dummy header
                    status[e.Slot].DMR_RxLC = new LC()
                    {
                        SrcId = e.SrcId,
                        DstId = e.DstId
                    };

                status[e.Slot].DMR_RxPILC = new PrivacyLC();
                Log.Logger.Debug($"({Config.Name}) TS {e.Slot + 1} [STREAM ID {e.StreamId}] RX_LC {FneUtils.HexDump(status[e.Slot].DMR_RxLC.GetBytes())}");
            }

            // if we can, use the PI LC from the PI voice header as to keep all options intact
            if ((e.FrameType == FrameType.DATA_SYNC) && (e.DataType == DMRDataType.VOICE_PI_HEADER))
            {
                PrivacyLC lc = FullLC.DecodePI(data);
                status[e.Slot].DMR_RxPILC = lc;
                Log.Logger.Information($"({Config.Name}) DMRD: Traffic *CALL PI PARAMS  * PEER {e.PeerId} DST_ID {e.DstId} TS {e.Slot + 1} ALGID {lc.AlgId} KID {lc.KId} [STREAM ID {e.StreamId}]");
                Log.Logger.Debug($"({Config.Name}) TS {e.Slot + 1} [STREAM ID {e.StreamId}] RX_PI_LC {FneUtils.HexDump(status[e.Slot].DMR_RxPILC.GetBytes())}");
            }

            if ((e.FrameType == FrameType.DATA_SYNC) && (e.DataType == DMRDataType.TERMINATOR_WITH_LC) && (status[e.Slot].RxType != FrameType.TERMINATOR))
            {
                callInProgress = false;
                TimeSpan callDuration = pktTime - status[0].RxStart;
                Log.Logger.Information($"({Config.Name}) DMRD: Traffic *CALL END       * PEER {e.PeerId} SRC_ID {e.SrcId} TGID {e.DstId} DUR {callDuration} [STREAM ID {e.StreamId}]");
            }

            if (e.FrameType == FrameType.VOICE_SYNC || e.FrameType == FrameType.VOICE)
            {
                byte[] ambe = new byte[FneSystemBase.DMR_AMBE_LENGTH_BYTES];
                Buffer.BlockCopy(data, 0, ambe, 0, 14);
                ambe[13] &= 0xF0;
                ambe[13] |= (byte)(data[19] & 0x0F);
                Buffer.BlockCopy(data, 20, ambe, 14, 13);
                DMRDecodeAudioFrame(ambe, e);
            }

            status[e.Slot].RxRFS = e.SrcId;
            status[e.Slot].RxType = e.FrameType;
            status[e.Slot].RxTGId = e.DstId;
            status[e.Slot].RxTime = pktTime;
            status[e.Slot].RxStreamId = e.StreamId;
        }
    }
}
