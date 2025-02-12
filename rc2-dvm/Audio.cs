using NAudio.Dsp;
using NAudio.Wave;
using NWaves.Filters.OnePole;
using NWaves.Operations;
using NWaves.Signals;
using NWaves.Transforms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace rc2_dvm
{
    public static class Utils
    {
        public static float[] PcmToFloat(short[] pcm16)
        {
            float[] floats = new float[pcm16.Length];
            for (int i = 0; i < pcm16.Length; i++)
            {
                float v = (float)pcm16[i] / (float)short.MaxValue;
                if (v > 1) { v = 1; }
                if (v < -1) { v = -1; }
                floats[i] = v;
            }
            return floats;
        }

        public static short[] FloatToPcm(float[] floats)
        {
            short[] pcm16 = new short[floats.Length];
            for (int i = 0; i < floats.Length; i++)
            {
                int v = (int)(floats[i] * short.MaxValue);
                if (v > short.MaxValue) { v = short.MaxValue; }
                if (v < short.MinValue) { v = -short.MinValue; }
                pcm16[i] = (short)v;
            }
            return pcm16;
        }

        public static short[] ResamplePcm(short[] pcm16, int inputSampleRate, int outputSampleRate)
        {
            // Don't do anything if the rates match
            if (inputSampleRate == outputSampleRate)
            {
                return pcm16;
            }
            Resampler resampler = new Resampler();
            float[] floats = PcmToFloat(pcm16);
            DiscreteSignal signal = new DiscreteSignal(inputSampleRate, floats, true);
            DiscreteSignal resampled = resampler.Resample(signal, outputSampleRate);
            return FloatToPcm(resampled.Samples);
        }
    }

    public class PitchDetector
    {
        private int avgSamples;

        private int sampleRate;

        private Stft stft;

        private float avgFreqHz;

        /// <summary>
        /// Average measured frequency
        /// </summary>
        public float AverageFreqHz
        {
            get
            {
                return avgFreqHz;
            }
        }

        /// <summary>
        /// Create a pitch detector which reports the running average of pitch for a sequence of samples
        /// </summary>
        /// <param name="avgSamples"></param>
        public PitchDetector(int bins, int sampleRate, int avgSamples = 5, int threshold = 10)
        {
            this.avgSamples = avgSamples;
            this.sampleRate = sampleRate;
            stft = new Stft(bins, bins / 4);
        }

        public void Measure(float[] samples)
        {
            // Creat signal
            DiscreteSignal signal = new DiscreteSignal(sampleRate, samples);
            // Analyze
            List<float[]> values = stft.Spectrogram(signal);
            // Determine if any
        }
    }
}
