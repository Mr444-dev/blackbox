using Blackbox.Core.Domain;

namespace Blackbox.UnitTests;

public sealed class WorkflowPayloadComparerTests
{
    [Fact]
    public void AreEquivalent_IgnoresWhitespaceAndObjectPropertyOrder()
    {
        const string original = "{\"workflow\":{\"id\":7,\"tags\":[\"a\",\"b\"]},\"enabled\":true}";
        const string jsonbRoundTrip = "{ \"enabled\": true, \"workflow\": { \"tags\": [\"a\", \"b\"], \"id\": 7 } }";

        Assert.True(WorkflowPayloadComparer.AreEquivalent(original, jsonbRoundTrip));
    }

    [Fact]
    public void AreEquivalent_RejectsDifferentValues()
    {
        Assert.False(WorkflowPayloadComparer.AreEquivalent("{\"quantity\":2}", "{\"quantity\":3}"));
    }

    [Fact]
    public void AreEquivalent_PreservesArrayOrder()
    {
        Assert.False(WorkflowPayloadComparer.AreEquivalent("{\"items\":[1,2]}", "{\"items\":[2,1]}"));
    }

    [Fact]
    public void AreEquivalent_RejectsInvalidJson()
    {
        Assert.False(WorkflowPayloadComparer.AreEquivalent("not-json", "not-json"));
    }
}
