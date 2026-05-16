using Lumina.Excel.Sheets;

namespace QuestForge.Adapters.Dalamud;

internal static class JobCategoryHelper
{
    internal static bool IsJobInCategory(ClassJobCategory category, uint classJobRowId) => classJobRowId switch
    {
        0  => category.ADV, 2  => category.GLA, 3  => category.PGL, 4  => category.MRD,
        5  => category.LNC, 6  => category.ARC, 7  => category.CNJ, 8  => category.THM,
        9  => category.CRP, 10 => category.BSM, 11 => category.ARM, 12 => category.GSM,
        13 => category.LTW, 14 => category.WVR, 15 => category.ALC, 16 => category.CUL,
        17 => category.MIN, 18 => category.BTN, 19 => category.FSH, 20 => category.PLD,
        21 => category.MNK, 22 => category.WAR, 23 => category.DRG, 24 => category.BRD,
        25 => category.WHM, 26 => category.BLM, 27 => category.ACN, 28 => category.SMN,
        29 => category.SCH, 30 => category.ROG, 31 => category.NIN, 32 => category.MCH,
        33 => category.DRK, 34 => category.AST, 35 => category.SAM, 36 => category.RDM,
        37 => category.BLU, 38 => category.GNB, 39 => category.DNC, 40 => category.RPR,
        41 => category.SGE, 42 => category.VPR, 43 => category.PCT, _  => false
    };
}
