using System.Reflection;
using System.Text.Json;
using ACRLiveTiming.Model;
using ACRLiveTiming.Web;
using Xunit;

namespace ACRLiveTiming.Tests;

/// <summary>SteamIDs and EOS PUIDs are kept for results submission only: the
/// /state JSON, the page (which bakes /state in, and from which the copy, CSV,
/// PNG and Discord exports and the public tunnel are all built) never carry them.</summary>
public class PrivacyTests
{
    // the serializer options the host uses for /state (MainWindow.JsonOpts)
    private static readonly JsonSerializerOptions StateJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly string[] Ids = [Fixtures.SteamOne, Fixtures.EosOne, Fixtures.SteamTwo, Fixtures.EosTwo];

    [Fact]
    public void StateCarriesNoAccountIds()
    {
        var board = Fixtures.Board();
        var view = board.BuildView();
        view.Events = [new EventView("Example League", "Summer Rally")];
        string json = JsonSerializer.Serialize(view, StateJson);

        Assert.Contains("Driver One", json);
        foreach (string id in Ids) Assert.DoesNotContain(id, json);
        Assert.DoesNotContain("steamId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eosPuid", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PageCarriesNoAccountIds()
    {
        var board = Fixtures.Board();
        string state = JsonSerializer.Serialize(board.BuildView(), StateJson);
        using var server = new WebServer(() => state);
        var render = typeof(WebServer).GetMethod("RenderIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string html = (string)render.Invoke(server, null)!;

        Assert.Contains("Driver One", html);
        foreach (string id in Ids) Assert.DoesNotContain(id, html);
    }

    [Fact]
    public void SubmissionCarriesThem()
    {
        var snapshot = Fixtures.Board().BuildSubmission(0);
        Assert.Contains(snapshot.Drivers, d => d.SteamId64 == Fixtures.SteamOne && d.EosPuid == Fixtures.EosOne);
        Assert.Contains(snapshot.Drivers, d => d.UsernameAtRun == "Driver Three" && d.SteamId64 == null && d.EosPuid == null);
    }
}
