namespace OpenVoiceSharp.AuthoritativeServer;

/// <summary>
/// Sliding anti-replay window for monotonic packet sequences.
/// Accepts out-of-order packets within a 64-sequence window.
/// </summary>
internal sealed class SequenceWindow
{
    private bool Initialized;
    private uint NewestSequence;
    private ulong SeenBitmap;

    public bool TryAccept(uint sequence)
    {
        if (!Initialized)
        {
            Initialized = true;
            NewestSequence = sequence;
            SeenBitmap = 1UL;
            return true;
        }

        if (sequence > NewestSequence)
        {
            uint shift = sequence - NewestSequence;
            SeenBitmap = shift >= 64 ? 1UL : (SeenBitmap << (int)shift) | 1UL;
            NewestSequence = sequence;
            return true;
        }

        uint delta = NewestSequence - sequence;
        if (delta >= 64)
            return false;

        ulong mask = 1UL << (int)delta;
        if ((SeenBitmap & mask) != 0)
            return false;

        SeenBitmap |= mask;
        return true;
    }
}
