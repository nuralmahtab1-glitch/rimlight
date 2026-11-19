using System.Diagnostics;

namespace Rimlight.Core.Diagnostics;

/// <summary>One monotonic clock shared by the audio and render threads, in seconds.</summary>
public static class MonoClock
{
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
