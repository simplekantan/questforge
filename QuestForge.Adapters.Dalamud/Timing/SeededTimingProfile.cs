using QuestForge.Adapters.Timing;

namespace QuestForge.Adapters.Dalamud.Timing;

// Log-normal timing: all delays sampled from LogNormal(mu, sigma) clamped to [min, max].
// Same seed → same sequence, enabling deterministic replay (Phase 7).
// Pure logic — no Dalamud services. Reseed per run via EngineHost.BeginRun.
public sealed class SeededTimingProfile : ITimingProfile
{
    private Random _rng;

    public SeededTimingProfile(int seed) => _rng = new Random(seed);

    // Reseed — replaces the RNG instance so the entire sequence restarts deterministically
    public void Reseed(int seed) => _rng = new Random(seed);

    // LogNormal helper: Box-Muller transform → N(0,1) → LogNormal(mu, sigma),
    // clamped to [minMs, maxMs] and converted to TimeSpan.
    private TimeSpan LogNormal(double mu, double sigma, double minMs, double maxMs)
    {
        // Box-Muller transform: exclude 0 to avoid log(0)
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = 1.0 - _rng.NextDouble();
        double z  = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        double ms = Math.Exp(mu + sigma * z);
        ms = Math.Clamp(ms, minMs, maxMs);
        return TimeSpan.FromMilliseconds(ms);
    }

    // ReactionDelay — time from stimulus appearance to first engine action
    public TimeSpan ReactionDelay(StimulusType stimulus) => stimulus switch
    {
        StimulusType.Dialogue        => LogNormal(mu: 5.8, sigma: 0.4, minMs: 200,  maxMs: 1200),
        StimulusType.NewWindow       => LogNormal(mu: 6.0, sigma: 0.4, minMs: 300,  maxMs: 1500),
        StimulusType.SceneTransition => LogNormal(mu: 6.5, sigma: 0.5, minMs: 500,  maxMs: 2500),
        StimulusType.QuestStart      => LogNormal(mu: 6.2, sigma: 0.4, minMs: 400,  maxMs: 2000),
        StimulusType.CombatStart     => LogNormal(mu: 5.5, sigma: 0.3, minMs: 150,  maxMs: 800),
        _                            => LogNormal(mu: 5.8, sigma: 0.4, minMs: 200,  maxMs: 1000),
    };

    // DecisionDelay — deliberation time scales with number of choices presented
    public TimeSpan DecisionDelay(int choiceCount)
        => LogNormal(
            mu:    5.8 + 0.2 * Math.Log(Math.Max(1, choiceCount)),
            sigma: 0.35,
            minMs: 300,
            maxMs: 3000);

    // InterActionGap — pause between consecutive interactions (click, confirm, etc.)
    public TimeSpan InterActionGap()
        => LogNormal(mu: 5.2, sigma: 0.3, minMs: 100, maxMs: 600);

    // ShouldTakeBreak — Phase 6: no break simulation
    // A real implementation would use context.TotalSessionDuration and context.TimeSinceLastBreak.
    public bool ShouldTakeBreak(SessionContext context) => false;

    // BreakDuration — log-normal in the 30 s – 5 min range
    public TimeSpan BreakDuration()
        => LogNormal(mu: 8.5, sigma: 0.5, minMs: 30_000, maxMs: 300_000);
}
