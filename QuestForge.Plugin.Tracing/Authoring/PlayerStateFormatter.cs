using System.Globalization;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;

namespace QuestForge.Plugin.Tracing.Authoring;

public static class PlayerStateFormatter
{
    public static string FormatZone(uint zoneId)
        => $"Zone: {zoneId}";

    public static string FormatCfc(ushort cfcId)
        => $"CFC: {cfcId}";

    public static string FormatPosition(WorldPosition pos)
        => string.Format(
            CultureInfo.InvariantCulture,
            "Position: ({0:F2}, {1:F2}, {2:F2})",
            pos.X, pos.Y, pos.Z);

    public static string FormatPositionJson(WorldPosition pos)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{{\"x\": {0:F2}, \"y\": {1:F2}, \"z\": {2:F2}}}",
            pos.X, pos.Y, pos.Z);

    public static string FormatJob(JobId job, string? jobName, int level)
    {
        if (job.Value == 0)
            return "Job: (none)";
        return string.IsNullOrEmpty(jobName)
            ? $"Job: ({job.Value}), Lv {level}"
            : $"Job: {jobName} ({job.Value}), Lv {level}";
    }

    public static string FormatMount(MountState mount)
        => $"Mount: {mount}";

    public static string FormatCombat(bool inCombat)
        => inCombat ? "Combat: Yes" : "Combat: No";

    public static string FormatHp(int hpCurrent, int hpMax)
        => hpMax == 0 ? "HP: (unknown)" : $"HP: {hpCurrent} / {hpMax}";

    public static string FormatInstance(InstanceKind kind)
        => $"Instance: {kind}";
}
