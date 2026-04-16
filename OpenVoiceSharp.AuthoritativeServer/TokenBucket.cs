namespace OpenVoiceSharp.AuthoritativeServer;

internal sealed class TokenBucket
{
    private readonly double FillRatePerSecond;
    private readonly double Capacity;
    private double Tokens;
    private DateTime LastRefillUtc;

    public TokenBucket(int fillRatePerSecond)
    {
        FillRatePerSecond = fillRatePerSecond;
        Capacity = fillRatePerSecond;
        Tokens = fillRatePerSecond;
        LastRefillUtc = DateTime.UtcNow;
    }

    public bool TryConsume(int tokens)
    {
        Refill();

        if (Tokens < tokens)
            return false;

        Tokens -= tokens;
        return true;
    }

    private void Refill()
    {
        DateTime now = DateTime.UtcNow;
        double elapsedSeconds = (now - LastRefillUtc).TotalSeconds;
        if (elapsedSeconds <= 0)
            return;

        Tokens = Math.Min(Capacity, Tokens + elapsedSeconds * FillRatePerSecond);
        LastRefillUtc = now;
    }
}
