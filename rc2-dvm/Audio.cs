﻿using NAudio.Dsp;
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
            return (float)Math.Pow((db / 20.0f), 10);
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

        // This is the tone detection ratio (amplitude of max bin divided by average of all others)
        private int detect_ratio;

        // This is the number of "hits" on a frequency we need to get before we detect a valid tone
        private int hits_reqd;

        // Counter for the above hits
        private int hits_freq;
        private int num_hits;

        // The STFT (short-time fourier transform) operator
        private Stft stft;

        /// <summary>
        /// Create a pitch detector which reports the running average of pitch for a sequence of samples
        /// </summary>
        /// <param name="detect_ratio">Ratio required for a valid tone detection</param>
        /// <param name="hits_reqd">Number of repeated "hits" on a frequency to count as a tone detection</param>
        public MBEToneDetector(int detect_ratio = 90, int hits_reqd = 2, int low_limit = 250, int high_limit = 3000)
        {
            this.detect_ratio = detect_ratio;
            this.hits_reqd = hits_reqd;
            stft = new Stft(window_size, 1, NWaves.Windows.WindowType.Hann, num_coeffs);
            hits_freq = 0;
            num_hits = 0;
            low_bin_limit = (int)(low_limit / bin_size_hz);
            high_bin_limit = (int)(high_limit / bin_size_hz);
        }

        /// <summary>
        /// Perform a tone analysis on the provided samples, and return a tone frequency if one is detected
        /// </summary>
        /// <param name="samples"></param>
        /// <returns></returns>
        public int Detect(DiscreteSignal signal)
        {
            // Validate input
            if (signal.Length != window_size)
            {
                throw new ArgumentOutOfRangeException($"Signal must be {window_size} samples long!");
            }
            if (signal.SamplingRate != sample_rate)
            {
                throw new ArgumentOutOfRangeException($"Signal must have sample rate of {sample_rate} Hz!");
            }
            
            // Analyze
            float[] values = stft.Spectrogram(signal)[0];

            // Remove bins outside our limit
            float[] limited_values = values[low_bin_limit..high_bin_limit];

            // Find max (from https://stackoverflow.com/a/50239922/1842613)
            (float max_val, int max_idx) = limited_values.Select((n, i) => (n, i)).Max();

            // Add back in our lower limit so the index is correct
            max_idx += low_bin_limit;

            // Calculate sum of all others
            float sum = values.Sum() - max_val;

            // Find average
            float avg = sum / (window_size - 1);

            // Find ratio
            float ratio = max_val / avg;

            // Debug
            //Log.Logger.Debug($"(Tone detector): max at {max_idx} ({(int)(max_idx * bin_size_hz)} Hz): {max_val}, ratio: {ratio}");

            // Return if above threshold
            if (ratio > detect_ratio)
            {
                // Calculate the tone frequency
                int tone_freq = (int)(bin_size_hz * max_idx);

                // Determine hits
                if (hits_freq == tone_freq)
                {
                    num_hits++;
                    if (num_hits >= hits_reqd)
                    {
                        // Debug
                        //Log.Logger.Debug($"Detected {tone_freq} Hz tone! (ratio {ratio})");
                        return tone_freq;
                    }
                }
                else
                {
                    num_hits = 1;
                    hits_freq = tone_freq;
                }
            }
            return 0;
        }
    }
}
