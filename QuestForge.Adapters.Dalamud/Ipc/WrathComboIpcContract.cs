namespace QuestForge.Adapters.Dalamud.Ipc;

/// <summary>
/// Wire-level constants for WrathCombo's IPC.
/// Derived from the WrathCombo IPC documentation and confirmed against the
/// IPC gate registrations in WrathCombo's Provider files.
/// These are our own definitions — QuestForge does NOT reference WrathCombo.API.
/// </summary>
internal static class WrathComboIpcContract
{
    // IPC gate names
    internal const string GateIPCReady                    = "WrathCombo.IPCReady";
    internal const string GateRegisterForLeaseWithCallback = "WrathCombo.RegisterForLeaseWithCallback";
    internal const string GateSetAutoRotationState        = "WrathCombo.SetAutoRotationState";
    internal const string GateSetCurrentJobAutoRotationReady = "WrathCombo.SetCurrentJobAutoRotationReady";
    internal const string GateSetAutoRotationConfigState  = "WrathCombo.SetAutoRotationConfigState";
    internal const string GateReleaseControl              = "WrathCombo.ReleaseControl";

    // SetResult int values (from WrathCombo.API/Enum/SetResult.cs)
    internal const int SetResultOkay          = 0;
    internal const int SetResultOkayWorking   = 1;
    internal const int SetResultDuplicate     = 13;

    // AutoRotationConfigOption int values (from WrathCombo.API/Enum/AutoRotationConfigOption.cs).
    // The four options QuestForge forces for reliable quest-mob engagement (matching Questionable):
    // DPS targeting = Manual, attack NPCs, and disable both "in combat only" gates so WrathCombo
    // will throw the first hit to pull a quest mob (pre-combat). Healer/rez/cleanse options are
    // intentionally NOT forced — those are left to the user's existing presets.
    internal const int ConfigInCombatOnly         = 0;
    internal const int ConfigDPSRotationMode      = 1;
    internal const int ConfigIncludeNPCs          = 12;
    internal const int ConfigOnlyAttackInCombat   = 13;

    // DPSRotationMode int values (from WrathCombo.API/Enum/DPSRotationMode.cs)
    internal const int DPSRotationModeManual = 0;

    /// <summary>Returns true for a SetResult int value that is considered success.</summary>
    internal static bool IsSetResultOk(int result) =>
        result is SetResultOkay or SetResultOkayWorking or SetResultDuplicate;
}
