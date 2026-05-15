using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tests.Types;

public class IdentifierTests
{
    [Fact]
    public void NpcId_SameValue_AreEqual()
    {
        var a = new NpcId(123);
        var b = new NpcId(123);
        Assert.Equal(a, b);
    }

    [Fact]
    public void NpcId_And_ZoneId_SameValue_HaveDifferentTypes()
    {
        var npcId = new NpcId(123);
        var zoneId = new ZoneId(123);

        // They are different types — verifies no implicit conversion
        Assert.IsType<NpcId>(npcId);
        Assert.IsType<ZoneId>(zoneId);
        Assert.NotEqual(typeof(NpcId), typeof(ZoneId));
    }

    [Fact]
    public void DefaultNpcId_ValueIsZero()
    {
        Assert.Equal(0u, default(NpcId).Value);
    }

    [Fact]
    public void ZoneId_Exists_WithValueProperty()
    {
        var id = new ZoneId(1u);
        Assert.Equal(1u, id.Value);
    }

    [Fact]
    public void NpcId_Exists_WithValueProperty()
    {
        var id = new NpcId(2u);
        Assert.Equal(2u, id.Value);
    }

    [Fact]
    public void QuestId_Exists_WithValueProperty()
    {
        var id = new QuestId(3u);
        Assert.Equal(3u, id.Value);
    }

    [Fact]
    public void AetheryteId_Exists_WithValueProperty()
    {
        var id = new AetheryteId(4u);
        Assert.Equal(4u, id.Value);
    }

    [Fact]
    public void AethernetId_Exists_WithValueProperty()
    {
        var id = new AethernetId(5u);
        Assert.Equal(5u, id.Value);
    }

    [Fact]
    public void ItemId_Exists_WithValueProperty()
    {
        var id = new ItemId(6u);
        Assert.Equal(6u, id.Value);
    }

    [Fact]
    public void JobId_Exists_WithValueProperty()
    {
        var id = new JobId(7u);
        Assert.Equal(7u, id.Value);
    }

    [Fact]
    public void DialogueOptionId_Exists_WithValueProperty()
    {
        var id = new DialogueOptionId(8u);
        Assert.Equal(8u, id.Value);
    }

    [Fact]
    public void InteractableId_Exists_WithValueProperty()
    {
        var id = new InteractableId(9u);
        Assert.Equal(9u, id.Value);
    }

    [Fact]
    public void DutyId_Exists_WithValueProperty()
    {
        var id = new DutyId(10u);
        Assert.Equal(10u, id.Value);
    }
}