using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace rc2_dvm
{
    /// <summary>
    /// Smooths bursty receive audio into a steady, real-time 20 ms stream for the RC2 console.
    /// </summary>
    internal sealed class RxAudioPacer : IDisposable
    {
        /// <summary>
        /// Sample rate of the audio passing through the pacer (the P25/DMR vocoder rate)
        /// </summary>
        public const int SampleRateHz = 8000;

        /// <summary>
        /// Samples per output frame (20 ms @ 8 kHz)
        /// </summary>
        public const int FrameSamples = 160;

        /// <summary>
        /// Duration of one output frame
        /// </summary>
        public const int FrameMs = 20;

        /// <summary>
        /// Most frames we'll emit in one go to catch up after the thread has been delayed (e.g. by a GC pause).
        /// Beyond this we skip ahead instead, so we never flood the console with a long burst.
        /// </summary>
        private const int MaxCatchUpFrames = 5;

        private enum PacerState
        {
            /// <summary>No audio queued, emitting silence</summary>
            Idle,
            /// <summary>Audio is queued but we're holding it back to build up the jitter buffer</summary>
            Buffering,
            /// <summary>Playing queued audio out in real time</summary>
            Playing
        }

        /// <summary>
        /// Counters describing what the pacer has been doing since the last reset
        /// </summary>
        internal readonly record struct PacerStats(
            int QueueMs,
            int PeakQueueMs,
            int Underruns,
            int OverflowMs,
            long FramesEmitted,
            long SilenceFrames);

        private readonly Action<short[], uint> sink;
        private readonly Func<bool> sinkEnabled;
        private readonly Func<long> clockMs;
        private readonly string name;
        private readonly int jitterBufferMs;

        private readonly object sync = new object();
        private readonly short[] ring;
        private int ringHead = 0;
        private int ringCount = 0;
        private PacerState state = PacerState.Idle;
        private long holdUntilMs = 0;
        private bool endOfStream = true;

        // Stats (guarded by sync)
        private int peakQueueSamples = 0;
        private int underruns = 0;
        private long overflowSamples = 0;
        private long framesEmitted = 0;
        private long silenceFrames = 0;

        private Thread? thread;
        private volatile bool running = false;
        private bool timerResolutionRaised = false;

        /// <summary>
        /// Create a new RX audio pacer
        /// </summary>
        /// <param name="name">name used in log messages</param>
        /// <param name="sink">called with each 160-sample frame and the sample rate (e.g. DVMRadio.SendRxPCM16Samples)</param>
        /// <param name="sinkEnabled">
        /// returns whether frames should actually be delivered right now (e.g. the console is connected). When it
        /// returns false the pacer keeps running in real time but discards the frames, so stale audio never builds up.
        /// </param>
        /// <param name="jitterBufferMs">
        /// how long to hold the first audio of a talkspurt before playing it. This is how late a following chunk of
        /// audio can be without causing a gap. It also adds this much latency to received audio.
        /// </param>
        /// <param name="maxQueueMs">most audio the pacer will queue; if more arrives, the oldest audio is dropped</param>
        /// <param name="clockMs">optional clock override (milliseconds), used for testing</param>
        public RxAudioPacer(string name, Action<short[], uint> sink, Func<bool> sinkEnabled,
            int jitterBufferMs = 60, int maxQueueMs = 1500, Func<long>? clockMs = null)
        {
            if (jitterBufferMs < 0) throw new ArgumentOutOfRangeException(nameof(jitterBufferMs));
            if (maxQueueMs < FrameMs * 2) throw new ArgumentOutOfRangeException(nameof(maxQueueMs));

            this.name = name;
            this.sink = sink;
            this.sinkEnabled = sinkEnabled;
            this.jitterBufferMs = jitterBufferMs;
            this.clockMs = clockMs ?? DefaultClockMs;
            ring = new short[maxQueueMs * (SampleRateHz / 1000)];
        }

        private static long DefaultClockMs() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

        /// <summary>
        /// Queue decoded audio for playout. May be called from any thread, with any number of samples.
        /// </summary>
        /// <param name="samples">PCM16 samples at 8 kHz</param>
        public void Enqueue(short[] samples)
        {
            if (samples == null || samples.Length == 0) return;

            lock (sync)
            {
                int offset = 0;
                int count = samples.Length;

                // If a single chunk is bigger than the whole queue, only the newest part of it can fit
                if (count > ring.Length)
                {
                    offset = count - ring.Length;
                    overflowSamples += offset;
                    count = ring.Length;
                }

                // If it doesn't fit alongside what's already queued, drop the oldest audio to make room
                int excess = ringCount + count - ring.Length;
                if (excess > 0)
                {
                    ringHead = (ringHead + excess) % ring.Length;
                    ringCount -= excess;
                    overflowSamples += excess;
                }

                // Copy in at the tail (in up to two segments, since the ring wraps)
                int tail = (ringHead + ringCount) % ring.Length;
                int first = Math.Min(count, ring.Length - tail);
                Array.Copy(samples, offset, ring, tail, first);
                if (first < count)
                {
                    Array.Copy(samples, offset + first, ring, 0, count - first);
                }
                ringCount += count;
                peakQueueSamples = Math.Max(peakQueueSamples, ringCount);

                // New audio means the stream is active again
                endOfStream = false;

                // Start of a talkspurt (or recovery from a gap): build up the jitter buffer before playing
                if (state == PacerState.Idle)
                {
                    state = PacerState.Buffering;
                    holdUntilMs = clockMs() + jitterBufferMs;
                }
            }
        }

        /// <summary>
        /// Tell the pacer that no more audio is expected for now (e.g. the call ended). Anything still queued plays out
        /// normally; the pacer just won't count running dry afterwards as an underrun.
        /// </summary>
        public void EndOfStream()
        {
            lock (sync)
            {
                endOfStream = true;
            }
        }

        /// <summary>
        /// Discard everything queued and go back to idle
        /// </summary>
        public void Clear()
        {
            lock (sync)
            {
                ringHead = 0;
                ringCount = 0;
                state = PacerState.Idle;
                endOfStream = true;
            }
        }

        /// <summary>
        /// Produce the next output frame. Always returns exactly FrameSamples samples, so the output stream is
        /// continuous: silence when there's no audio, queued audio when there is.
        /// </summary>
        /// <param name="nowMs">the current time in milliseconds</param>
        /// <param name="isSilence">true if the frame is padding, false if it carries queued audio</param>
        internal short[] Step(long nowMs, out bool isSilence)
        {
            lock (sync)
            {
                // Finish the jitter hold if it's done
                if (state == PacerState.Buffering)
                {
                    if (ringCount == 0)
                        state = PacerState.Idle;
                    else if (nowMs >= holdUntilMs)
                        state = PacerState.Playing;
                }

                // Idle, or still holding: emit silence
                if (state != PacerState.Playing)
                {
                    isSilence = true;
                    silenceFrames++;
                    framesEmitted++;
                    return new short[FrameSamples];
                }

                // Playing, but we've run dry
                if (ringCount == 0)
                {
                    // Running dry mid-call means a late chunk: count it, and go back to buffering when audio returns.
                    // Running dry at the end of a call is normal.
                    if (!endOfStream)
                        underruns++;
                    state = PacerState.Idle;
                    isSilence = true;
                    silenceFrames++;
                    framesEmitted++;
                    return new short[FrameSamples];
                }

                // Playing: take a frame from the queue. A short tail is padded with silence.
                short[] frame = new short[FrameSamples];
                int take = Math.Min(ringCount, FrameSamples);
                int firstPart = Math.Min(take, ring.Length - ringHead);
                Array.Copy(ring, ringHead, frame, 0, firstPart);
                if (firstPart < take)
                {
                    Array.Copy(ring, 0, frame, firstPart, take - firstPart);
                }
                ringHead = (ringHead + take) % ring.Length;
                ringCount -= take;

                isSilence = false;
                framesEmitted++;
                return frame;
            }
        }

        /// <summary>
        /// Get the pacer's counters
        /// </summary>
        /// <param name="reset">whether to reset the peak/underrun/overflow counters afterwards</param>
        internal PacerStats GetStats(bool reset = false)
        {
            lock (sync)
            {
                PacerStats stats = new PacerStats(
                    ringCount / (SampleRateHz / 1000),
                    peakQueueSamples / (SampleRateHz / 1000),
                    underruns,
                    (int)(overflowSamples / (SampleRateHz / 1000)),
                    framesEmitted,
                    silenceFrames);
                if (reset)
                {
                    peakQueueSamples = ringCount;
                    underruns = 0;
                    overflowSamples = 0;
                }
                return stats;
            }
        }

        /// <summary>
        /// Start the pacer thread
        /// </summary>
        public void Start()
        {
            if (running) return;
            running = true;

            // On Windows the default timer tick is ~15.6 ms, which makes a 20 ms schedule very ragged. Ask for 1 ms.
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    timeBeginPeriod(1);
                    timerResolutionRaised = true;
                }
                catch (Exception ex)
                {
                    Log.Logger.Debug(ex, "({0:l}) Could not raise the system timer resolution", name);
                }
            }

            thread = new Thread(Run)
            {
                Name = $"RxAudioPacer-{name}",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            thread.Start();
        }

        /// <summary>
        /// Stop the pacer thread
        /// </summary>
        public void Stop()
        {
            running = false;
            Thread? t = thread;
            thread = null;
            if (t != null && t != Thread.CurrentThread)
            {
                t.Join(500);
            }
            if (timerResolutionRaised)
            {
                timerResolutionRaised = false;
                try { timeEndPeriod(1); } catch { /* nothing useful to do */ }
            }
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// Thread body: emit one frame every FrameMs, on an absolute schedule so that timing error never accumulates
        /// </summary>
        private void Run()
        {
            long next = clockMs();
            while (running)
            {
                // Sleep until the next frame is due
                long wait = next - clockMs();
                if (wait > 0)
                {
                    Thread.Sleep((int)wait);
                    continue;
                }

                // Emit every frame that's due (more than one if we were delayed), but not an unbounded number
                int emitted = 0;
                long now = clockMs();
                while (next <= now && emitted < MaxCatchUpFrames && running)
                {
                    EmitFrame(next);
                    next += FrameMs;
                    emitted++;
                }

                // If we're still behind after the catch-up limit, skip ahead rather than burst
                if (next <= now)
                {
                    next = now + FrameMs;
                }
            }
        }

        private void EmitFrame(long nowMs)
        {
            short[] frame = Step(nowMs, out _);
            try
            {
                if (sinkEnabled())
                {
                    sink(frame, (uint)SampleRateHz);
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "({0:l}) RX audio sink threw an exception", name);
            }
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint timeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint timeEndPeriod(uint milliseconds);
    }
}