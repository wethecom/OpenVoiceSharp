namespace OpenVoiceSharp
{
    /// <summary>
    /// Per-speaker jitter buffer for packet reordering and limited loss tolerance.
    /// </summary>
    public sealed class VoiceJitterBuffer
    {
        private readonly SortedDictionary<uint, byte[]> Packets = new();
        private readonly int TargetDelayPackets;
        private readonly int MaxBufferPackets;

        private bool Started;
        private uint ExpectedSequence;
        private int MissingSequenceSkips;

        public VoiceJitterBuffer(int targetDelayPackets = 3, int maxBufferPackets = 24)
        {
            if (targetDelayPackets < 1)
                throw new ArgumentOutOfRangeException(nameof(targetDelayPackets));
            if (maxBufferPackets < targetDelayPackets + 2)
                throw new ArgumentOutOfRangeException(nameof(maxBufferPackets));

            TargetDelayPackets = targetDelayPackets;
            MaxBufferPackets = maxBufferPackets;
        }

        public void Add(uint sequence, byte[] payload, int length)
        {
            if (payload is null)
                throw new ArgumentNullException(nameof(payload));
            if (length <= 0 || length > payload.Length)
                throw new ArgumentOutOfRangeException(nameof(length));

            if (Started && sequence < ExpectedSequence)
                return;
            if (Packets.ContainsKey(sequence))
                return;

            byte[] copy = new byte[length];
            Buffer.BlockCopy(payload, 0, copy, 0, length);
            Packets[sequence] = copy;

            // Prevent unbounded growth under extreme disorder.
            while (Packets.Count > MaxBufferPackets)
            {
                uint oldest = GetFirstKey();
                Packets.Remove(oldest);
            }
        }

        public IEnumerable<(uint sequence, byte[] payload)> DrainReady()
        {
            if (!Started)
            {
                if (Packets.Count < TargetDelayPackets)
                    yield break;

                ExpectedSequence = GetFirstKey();
                Started = true;
                MissingSequenceSkips = 0;
            }

            while (Packets.Count > 0)
            {
                if (Packets.TryGetValue(ExpectedSequence, out byte[]? payload))
                {
                    Packets.Remove(ExpectedSequence);
                    uint sequence = ExpectedSequence;
                    ExpectedSequence++;
                    MissingSequenceSkips = 0;
                    yield return (sequence, payload);
                    continue;
                }

                bool forceAdvance = Packets.Count >= MaxBufferPackets || MissingSequenceSkips >= TargetDelayPackets;
                if (!forceAdvance)
                    yield break;

                ExpectedSequence++;
                MissingSequenceSkips++;
            }
        }

        private uint GetFirstKey()
        {
            using IEnumerator<uint> enumerator = Packets.Keys.GetEnumerator();
            if (!enumerator.MoveNext())
                throw new InvalidOperationException("Jitter buffer is empty.");

            return enumerator.Current;
        }
    }
}
