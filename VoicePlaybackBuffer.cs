namespace OpenVoiceSharp
{
    /// <summary>
    /// Thread-safe PCM playback buffer with partial-read support and silence fill.
    /// Useful for audio callbacks that request fixed-size chunks.
    /// </summary>
    public sealed class VoicePlaybackBuffer
    {
        private readonly Queue<ArraySegment<byte>> Segments = new();
        private readonly object Sync = new();
        private int AvailableBytes;

        /// <summary>
        /// Number of queued PCM bytes available to read.
        /// </summary>
        public int Available
        {
            get
            {
                lock (Sync)
                    return AvailableBytes;
            }
        }

        /// <summary>
        /// Enqueues decoded PCM bytes.
        /// </summary>
        public void Enqueue(byte[] pcmData, int length)
        {
            if (pcmData is null)
                throw new ArgumentNullException(nameof(pcmData));
            if (length <= 0 || length > pcmData.Length)
                throw new ArgumentOutOfRangeException(nameof(length));

            byte[] copy = new byte[length];
            Buffer.BlockCopy(pcmData, 0, copy, 0, length);

            lock (Sync)
            {
                Segments.Enqueue(new ArraySegment<byte>(copy, 0, copy.Length));
                AvailableBytes += copy.Length;
            }
        }

        /// <summary>
        /// Reads up to <paramref name="count"/> bytes into <paramref name="output"/>.
        /// Any missing bytes are filled with zero (silence).
        /// Returns the amount of real PCM bytes copied before silence fill.
        /// </summary>
        public int ReadAndFillSilence(byte[] output, int count, int offset = 0)
        {
            if (output is null)
                throw new ArgumentNullException(nameof(output));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (offset < 0 || offset > output.Length - count)
                throw new ArgumentOutOfRangeException(nameof(offset));

            int copied = 0;
            lock (Sync)
            {
                while (copied < count && Segments.Count > 0)
                {
                    ArraySegment<byte> current = Segments.Dequeue();
                    int take = Math.Min(current.Count, count - copied);

                    Buffer.BlockCopy(current.Array!, current.Offset, output, offset + copied, take);
                    copied += take;
                    AvailableBytes -= take;

                    int remaining = current.Count - take;
                    if (remaining > 0)
                    {
                        Segments.Enqueue(new ArraySegment<byte>(current.Array!, current.Offset + take, remaining));
                    }
                }
            }

            if (copied < count)
                Array.Clear(output, offset + copied, count - copied);

            return copied;
        }

        /// <summary>
        /// Drains and returns all queued bytes.
        /// </summary>
        public byte[] Flush()
        {
            lock (Sync)
            {
                if (AvailableBytes == 0)
                    return Array.Empty<byte>();

                byte[] output = new byte[AvailableBytes];
                int offset = 0;
                while (Segments.Count > 0)
                {
                    ArraySegment<byte> segment = Segments.Dequeue();
                    Buffer.BlockCopy(segment.Array!, segment.Offset, output, offset, segment.Count);
                    offset += segment.Count;
                }

                AvailableBytes = 0;
                return output;
            }
        }

        /// <summary>
        /// Clears all queued PCM bytes.
        /// </summary>
        public void Clear()
        {
            lock (Sync)
            {
                Segments.Clear();
                AvailableBytes = 0;
            }
        }
    }
}
