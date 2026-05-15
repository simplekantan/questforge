using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Tests.Types;

public class ResultTests
{
    [Fact]
    public void Ok_WithValue_IsSuccessTrue()
    {
        var result = Result.Ok(42);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Ok_WithValue_ValueOrDefaultReturnsValue()
    {
        var result = Result.Ok(42);
        Assert.Equal(42, result.ValueOrDefault);
    }

    [Fact]
    public void Ok_WithValue_ValueOrThrowReturnsValue()
    {
        var result = Result.Ok(42);
        Assert.Equal(42, result.ValueOrThrow);
    }

    [Fact]
    public void Fail_IsSuccessFalse()
    {
        var result = Result.Fail<int>("not-found");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Fail_ValueOrDefaultReturnsDefault()
    {
        var result = Result.Fail<int>("not-found");
        Assert.Equal(0, result.ValueOrDefault);
    }

    [Fact]
    public void Fail_ValueOrThrowThrowsInvalidOperationException()
    {
        var result = Result.Fail<int>("not-found");
        Assert.Throws<InvalidOperationException>(() => result.ValueOrThrow);
    }

    [Fact]
    public void TwoOk_WithSameValue_AreEqual()
    {
        var a = Result.Ok(42);
        var b = Result.Ok(42);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Fail_WithDifferentDetail_AreNotEqual()
    {
        var a = Result.Fail<int>("x", "a");
        var b = Result.Fail<int>("x", "b");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void OkVoid_IsSuccessTrue()
    {
        var result = Result.Ok();
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void OkVoid_IsResultUnitSuccess()
    {
        Result<Unit> result = Result.Ok();
        Assert.IsType<Result<Unit>.Success>(result);
    }

    [Fact]
    public void FailVoid_IsSuccessFalse()
    {
        var result = Result.Fail("reason");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void FailVoid_ReasonIsPreserved()
    {
        var result = Result.Fail("reason");
        var failure = Assert.IsType<Result<Unit>.Failure>(result);
        Assert.Equal("reason", failure.Reason);
    }

    [Fact]
    public void PatternMatching_Success_AccessesValueCorrectly()
    {
        Result<int> result = Result.Ok(99);
        if (result is Result<int>.Success s)
        {
            Assert.Equal(99, s.Value);
        }
        else
        {
            Assert.Fail("Expected Success but got Failure");
        }
    }
}