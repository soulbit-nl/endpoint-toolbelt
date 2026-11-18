using EndpointToolbelt.App.Core;
using Xunit;

namespace EndpointToolbelt.Tests;

public class CardTextTests
{
    [Theory]
    [InlineData("Company Portal is not installed.", "Company Portal is not installed.")]
    [InlineData("The IME is not installed", "The IME is not installed.")]
    [InlineData("Restart now!", "Restart now!")]
    [InlineData("", "")]
    public void A_card_title_ends_with_exactly_one_period(string title, string expected)
    {
        Assert.Equal(expected, ResultCard.Sentence(title));
    }

    [Fact]
    public void The_card_read_aloud_has_no_double_period()
    {
        var card = ResultCard.From(FindingSeverity.Warning, "Company Portal is not installed.", "Company Portal was not running.");
        Assert.DoesNotContain("..", card.AccessibleText);
        Assert.StartsWith("WARNING: Company Portal is not installed. Company Portal was not running.", card.AccessibleText);
    }
}
