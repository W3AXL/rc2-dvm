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
using Serilog;
using NAudio.SoundFont;

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
                if (v < short.MinValue) { v = short.MinValue; }
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

        /// <summary>
        /// Converts a linear gain value to its equivalent voltage gain DB value (20 * log10)
        /// </summary>
        /// <param name="linear">linear gain value</param>
        /// <returns></returns>
        public static float LinearToDB(float linear)
        {
            return 20.0f * (float)Math.Log10(linear);
        }

        /// <summary>
        /// Converts a DB gain value to its equivalent linear gain (10 ^ (/20))
        /// </summary>
        /// <param name="db"></param>
        /// <returns></returns>
        public static float DBtoLinear(float db)
        {
            return (float)Math.Pow(10, (db / 20.0f));
        }
    }

    public class MBEToneDetector
    {
        // Samplerate is 8000 Hz
        private static int sample_rate = 8000;

        // We operate on 160 sample (20ms @ 8kHz) windows
        private static int window_size = 160;

        // There are 128 possible tone indexes per TIA-102.BABA-1
        private static int num_coeffs = 128;

        // Bin size in hz
        private static float bin_size_hz = (float)sample_rate / 2f / (float)num_coeffs;

        // Bounds of tone detection (in bin index format)
        private int low_bin_limit;
        private int high_bin_limit;

        // The STFT (short-time fourier transform) operator
        private Stft stft;

        // Tracker for last tone detected
        private int last_tone = 0;

        /// <summary>
        /// Detector threshold for detecting the start of a tone
        /// </summary>
        private const float TONE_START_THRESHOLD = 0.6f;
        /// <summary>
        /// Detector threshold for detecting the end of a tone (allows for hysteresis)
        /// </summary>
        private const float TONE_END_THRESHOLD = 0.3f;
        /// <summary>
        /// Minimum RMS signal power required to perform tone detection
        /// </summary>
        private const float MIN_SIGNAL_RMS = 0.02f;

        /// <summary>
        /// Returns the fraction (out of 1) of a frame's energy that consists of the desired tone frequency in Hz
        /// </summary>
        /// <param name="samples"></param>
        /// <param name="tone_hz"></param>
        /// <returns></returns>
        public static float GetToneFraction(float[] samples, float tone_hz)
        {
            // Calculate squared sum total energy of the sample array
            double total = 1e-9;
            foreach (float sample in samples) total += (double)sample * (double)sample;

            // Perform Goertzel algoritm at 3 frequencies (the current tone frequency and one bin on either side) and get the max power 
            double maxPwr = 0;
            for (int k = -1; k <= 1; k++)
            {
                // Convert frequency to radians/s
                double w = 2.0 * Math.PI * (tone_hz + (k * bin_size_hz)) / sample_rate;
                // Calculate cosine coefficient
                double c = 2.0 * Math.Cos(w);
                // Initialize s1 & s2 state tracking vars
                double s1 = 0;
                double s2 = 0;
                // Iterate over samples and perform the calculation
                foreach (float sample in samples)
                {
                    double s = sample + c * s1 - s2;
                    s2 = s1;
                    s1 = s; 
                }
                // Calculate the total power of the bin
                double p = s1 * s1 + s2 * s2 - c * s1 * s2;
                // Check if this is a new max
                maxPwr = Math.Max(maxPwr, 2.0 * p / (samples.Length * total));
            }
            // Return the max power measured by the algo
            return (float)maxPwr;
        }

        /// <summary>
        /// Create a tone detector with hysteresis for detecting tones within a range from an audio signal
        /// </summary>
        /// <param name="low_limit_hz"></param>
        /// <param name="high_limit_hz"></param>
        public MBEToneDetector(int low_limit_hz = 250, int high_limit_hz = 3000)
        {
            // Init the singletone fourier transform
            stft = new Stft(window_size, 1, NWaves.Windows.WindowType.Hann, num_coeffs);
            // Calculate our bin limits from the low & high frequency limits
            low_bin_limit = (int)(low_limit_hz / bin_size_hz);
            high_bin_limit = (int)(high_limit_hz / bin_size_hz);
        }

        /// <summary>
        /// Perform gated tone detection on an audio signal and return the detected tone frequency in Hz, or 0 if no tone detected
        /// </summary>
        /// <param name="signal"></param>
        /// <returns></returns>
        public int Detect(DiscreteSignal signal)
        {
            // Validate signal length & samplerate
            if (signal.Length != window_size)
                throw new ArgumentOutOfRangeException($"Signal must be {window_size} samples long");
            if (signal.SamplingRate != sample_rate)
                throw new ArgumentOutOfRangeException($"Signal samplerate must be {sample_rate}");

            // Detect silence and ignore
            double e = 0;
            // Sum of squares for total signal energy
            foreach (float v in signal.Samples) e += (double)v * v;
            // Check if our RMS signal is above the threshold
            if (Math.Sqrt(e / signal.Length) < MIN_SIGNAL_RMS)
            {
                // Reset
                last_tone = 0;
                return 0;
            }

            // Find the peak tone frequency of this signal
            float[] values = stft.Spectrogram(signal)[0];
            int peak_idx = low_bin_limit;
            for (int i = low_bin_limit; i < high_bin_limit; i++)
            {
                if (values[i] > values[peak_idx]) peak_idx = i;
            }
            // Get freq from peak index
            int freq = (int)(peak_idx * bin_size_hz);

            // Tolerance for one bin on either side of the last tone, so we don't constantly flutter around
            if (last_tone != 0 && Math.Abs(freq - last_tone) <= (int)(bin_size_hz * 1.01f))
                freq = last_tone;

            // Now measure the goertzel fraction for the detected tone frequency
            float frac = GetToneFraction(signal.Samples, freq);

            // Verbose tone detector logging
            Log.Logger.Verbose("TONE FRAC {0:0.000}", frac);

            // Determine which threshold we should be using
            float threshold = (last_tone != 0) ? TONE_END_THRESHOLD : TONE_START_THRESHOLD;

            // Check threshold
            if (frac >= threshold)
            {
                last_tone = freq;
                return freq;
            }

            // Return 0 if tone detection failed
            last_tone = 0;
            return 0;
        }

        public void Reset()
        {
            last_tone = 0;
        }
    }
}
