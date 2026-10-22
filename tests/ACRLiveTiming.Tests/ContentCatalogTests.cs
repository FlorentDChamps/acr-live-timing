using ACRLiveTiming.Content;
using ACRLiveTiming.Decode;
using Xunit;

namespace ACRLiveTiming.Tests;

public class ContentCatalogTests
{
    [Theory]
    [InlineData("WelesS3HafrenNorthCut1Forward", "weless3hafrennorthcut1forward")]
    [InlineData("Group2/4", "group24")]
    [InlineData("WT_CLEAR", "wtclear")]
    [InlineData("Citroën", "citroen")]
    public void ReducesLikeTheCatalog(string value, string reduced)
        => Assert.Equal(reduced, ContentCatalog.Reduce(value));

    [Fact]
    public void ShowsTheGameNamesNotTheTranslations()
    {
        Assert.Equal("Vallée de Munster Montée", ContentCatalog.StageName("AlsaceS2MunsterFullForward"));
        Assert.Equal("Hyundai i20 Rally2", ContentCatalog.CarName("Hyundaii20NRally2"));
        Assert.Equal("Hyundai i20 Rally2", ContentCatalog.CarName("HyundaiI20NRally2"));
    }

    [Fact]
    public void FindsAWireRouteAndItsTrack()
    {
        // the game spells Wales routes "Weles…" and their level "Wales…"
        Assert.True(ContentCatalog.IsKnownRoute("WelesS3HafrenNorthCut1Forward"));
        Assert.NotNull(ContentCatalog.StageLengthKm("WelesS3HafrenNorthCut1Forward"));
        Assert.True(Names.VariantExtends("WelesS3HafrenNorthCut1Forward", "WalesS3HafrenNorth"));
        Assert.False(Names.VariantExtends("WelesS3HafrenNorthCut1Forward", "WalesS4HafrenSouth"));
        Assert.Equal("AlsaceS2Munster", ContentCatalog.StageName("AlsaceS2Munster"));   // a bare level stays raw
    }

    [Theory]
    [InlineData("SwitzerlAnd", "ch", "Switzerland")]
    [InlineData("FinlAnd", "fi", "Finland")]
    [InlineData("Wales", "gb-wls", "Wales")]
    public void ResolvesNationalities(string token, string flag, string name)
    {
        Assert.Equal(flag, ContentCatalog.CountryFlag(token));
        Assert.Equal(name, ContentCatalog.CountryName(token));
    }

    [Fact]
    public void OtherHasNoFlag()
    {
        Assert.Null(ContentCatalog.CountryFlag("Other"));
        Assert.Equal("Other", ContentCatalog.CountryName("Other"));
    }

    [Fact]
    public void NamesTheWeatherFromItsEnumValue()
    {
        Assert.Equal("Clear", WeatherForecast.TypeName(0));
        Assert.Equal("Light fog", WeatherForecast.TypeName(3));
        Assert.Equal("Blizzard", WeatherForecast.TypeName(10));
    }
}
