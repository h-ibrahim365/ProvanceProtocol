namespace Provance.Core.Tests.TestSupport
{
    /// <summary>A clock the test moves by hand. Timers still run on real time.</summary>
    internal sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
