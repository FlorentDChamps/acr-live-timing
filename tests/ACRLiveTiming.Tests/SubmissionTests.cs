using System.Text;
using System.Text.Json;
using ACRLiveTiming.Submission;
using Xunit;

namespace ACRLiveTiming.Tests;

public class PairingKeyTests
{
    private static string Key(string json)
        => PairingKey.Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string KeyFor(string url, string code = "7K9M-4QPX")
        => Key($$"""{"schemaVersion":"acr-pairing-key/1.0","serverUrl":"{{url}}","pairingCode":"{{code}}"}""");

    [Fact]
    public void DecodesAValidKey()
    {
        Assert.True(PairingKey.TryParse(KeyFor("https://results.example.org/acr"), out var key, out _));
        Assert.Equal("results.example.org", key!.Host);
        Assert.Equal("7K9M-4QPX", key.PairingCode);
    }

    [Fact]
    public void ShowsANonDefaultPort()
    {
        Assert.True(PairingKey.TryParse(KeyFor("https://203.0.113.10:8443"), out var key, out _));
        Assert.Equal("203.0.113.10:8443", key!.Host);
    }

    [Theory]
    [InlineData("https://results.example.org/")]          // trailing slash
    [InlineData("https://user:pass@results.example.org")] // user info
    [InlineData("https://results.example.org?a=1")]       // query
    [InlineData("ftp://results.example.org")]
    [InlineData("http://results.example.org")]            // plain http to a public name
    [InlineData("http://203.0.113.10:8080")]              // plain http to a public address
    public void RefusesABadServerUrl(string url)
        => Assert.False(PairingKey.TryParse(KeyFor(url), out _, out _));

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://192.168.1.20:8080")]
    [InlineData("http://10.0.0.5")]
    [InlineData("http://172.16.4.2:9000")]
    public void PlainHttpToATestServerOnlyInDebug(string url)
    {
#if DEBUG
        Assert.True(PairingKey.TryParse(KeyFor(url), out _, out _));
#else
        Assert.False(PairingKey.TryParse(KeyFor(url), out _, out _));
#endif
    }

    [Theory]
    [InlineData("xyz:abc")]
    [InlineData("acrlt1:***")]
    [InlineData("acrlt1:bm90IGpzb24")]  // "not json"
    public void RefusesDamagedKeys(string text)
        => Assert.False(PairingKey.TryParse(text, out _, out _));

    [Fact]
    public void RefusesAnotherVersion()
    {
        string key = Key("""{"schemaVersion":"acr-pairing-key/2.0","serverUrl":"https://a.example","pairingCode":"X"}""");
        Assert.False(PairingKey.TryParse(key, out _, out string error));
        Assert.Contains("version", error);
    }

    [Fact]
    public void SameKeySameHash()
    {
        PairingKey.TryParse(KeyFor("https://a.example"), out var a, out _);
        PairingKey.TryParse(KeyFor("https://a.example"), out var b, out _);
        PairingKey.TryParse(KeyFor("https://a.example", "OTHER"), out var c, out _);
        Assert.Equal(a!.Hash, b!.Hash);
        Assert.NotEqual(a.Hash, c!.Hash);
    }
}

public class ExchangeTests
{
    private static PairingKey Server()
    {
        string json = """{"schemaVersion":"acr-pairing-key/1.0","serverUrl":"https://results.example.org","pairingCode":"C"}""";
        string text = PairingKey.Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.True(PairingKey.TryParse(text, out var key, out _));
        return key!;
    }

    [Fact]
    public void ReadsAFullAnswer()
    {
        var result = SubmissionClient.ParseExchange(Server(), """
            {"schemaVersion":"acr-pairing-response/1.0","submissionToken":"tok",
             "submissionUrl":"https://results.example.org/v1/ingest/rally-results",
             "eventId":"summer-r03","eventName":"Summer Rally","serverName":"Example League",
             "expiresAt":"2026-08-16T18:00:00Z","authorizationId":"A1",
             "eventSettings":{"penaltyCapPct":50,"dnfNoRejoin":true}}
            """);
        var a = result.Association!;
        Assert.Equal("tok", result.Token);
        Assert.Equal("summer-r03", a.EventId);
        Assert.Equal("Example League · results.example.org — Summer Rally", a.Label);
        Assert.Equal("Example League", a.PublicServer);
        Assert.Equal(50, a.EventSettings!.PenaltyCapPct);
        Assert.True(a.EventSettings.DnfNoRejoin);
    }

    [Fact]
    public void AcceptsGroupedEventFields()
    {
        var result = SubmissionClient.ParseExchange(Server(), """
            {"submissionToken":"t","submissionUrl":"https://results.example.org/s","event":{"eventId":"e1","name":"Rally"}}
            """);
        Assert.Equal("e1", result.Association!.EventId);
        Assert.Equal("Rally", result.Association.EventName);
    }

    [Theory]
    [InlineData("""{"submissionToken":"t","submissionUrl":"https://results.example.org/s","eventId":42}""")]
    [InlineData("""{"submissionToken":"t","submissionUrl":"https://other.example.org/s","eventId":"e"}""")]
    [InlineData("""{"submissionToken":"t","submissionUrl":"https://results.example.org:8443/s","eventId":"e"}""")]
    [InlineData("""{"submissionUrl":"https://results.example.org/s","eventId":"e"}""")]
    [InlineData("""{"submissionToken":"t","submissionUrl":"https://results.example.org/s","eventId":""}""")]
    public void RefusesABadAnswer(string body)
        => Assert.Null(SubmissionClient.ParseExchange(Server(), body).Association);

    [Fact]
    public void RefusesATooLongEventId()
    {
        string body = $$"""{"submissionToken":"t","submissionUrl":"https://results.example.org/s","eventId":"{{new string('e', 129)}}"}""";
        Assert.Null(SubmissionClient.ParseExchange(Server(), body).Association);
    }

    [Fact]
    public void IgnoresInvalidEventSettings()
    {
        var result = SubmissionClient.ParseExchange(Server(), """
            {"submissionToken":"t","submissionUrl":"https://results.example.org/s","eventId":"e",
             "eventSettings":{"penaltyCapPct":900,"dnfNoRejoin":"yes","startTime":"20:00"}}
            """);
        Assert.NotNull(result.Association);
        Assert.Null(result.Association!.EventSettings);
        Assert.Equal("penaltyCapPct, dnfNoRejoin", string.Join(", ", result.IgnoredSettings!));
    }

    [Fact]
    public void ShowsNamesAsPlainTextTruncated()
    {
        var result = SubmissionClient.ParseExchange(Server(), $$"""
            {"submissionToken":"t","submissionUrl":"https://results.example.org/s","eventId":"e",
             "eventName":"Line\u0007one\nand {{new string('x', 300)}}"}
            """);
        string label = result.Association!.PublicEvent;
        Assert.DoesNotContain('\u0007', label);
        Assert.DoesNotContain('\n', label);
        Assert.Equal(200, label.Length);
        Assert.EndsWith("…", label);
    }
}

public class ResponseTests
{
    [Fact]
    public void AbsentAssociationMeansActive()
    {
        var r = SubmissionClient.Interpret(202, "{}");
        Assert.True(r.Stored);
        Assert.True(r.Active);
    }

    [Fact]
    public void OnlyEndedEndsTheAssociation()
    {
        Assert.False(SubmissionClient.Interpret(202, """{"association":"ended"}""").Active);
        Assert.True(SubmissionClient.Interpret(202, """{"association":"paused"}""").Active);
    }

    [Fact]
    public void ActiveKeepsTheAssociation()
        => Assert.True(SubmissionClient.Interpret(200, """{"association":"active","message":"Stored: 3 drivers."}""").Active);

    [Fact]
    public void ShowsTheServerMessage()
        => Assert.Equal("Stored — Stored: 3 drivers.", SubmissionClient.Interpret(202, """{"association":"ended","message":"Stored: 3 drivers."}""").Message);

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void RefusedTokenIsDeleted(int status)
        => Assert.True(SubmissionClient.Interpret(status, "").TokenRefused);

    [Fact]
    public void ErrorMessageIsCleanedAndCapped()
    {
        var r = SubmissionClient.Interpret(422, JsonSerializer.Serialize(new { error = "domain_rejected", message = new string('m', 400) }));
        Assert.False(r.Stored);
        Assert.False(r.TokenRefused);
        Assert.True(r.Message.Length < 330);
    }
}

public class PayloadTests
{
    private static JsonElement Doc(byte[] body) => JsonDocument.Parse(body).RootElement;

    private static Association Destination(bool pairedBefore)
        => new()
        {
            ServerUrl = "https://results.example.org",
            SubmissionUrl = "https://results.example.org/s",
            EventId = "summer-r03",
            PairedTimestamp = pairedBefore ? null : long.MaxValue
        };

    [Fact]
    public void ClassifiesCellsStrictly()
    {
        var snapshot = Fixtures.Board().BuildSubmission(0);
        Assert.Empty(snapshot.Problems);
        Assert.Equal(2, snapshot.Stages.Count);
        Assert.Equal("AlsaceS4SaverneFullForward", snapshot.Stages[0].RouteId);
        Assert.Equal("WelesS3HafrenNorthFullForward", snapshot.Stages[1].RouteId);

        var one = snapshot.Drivers.Single(d => d.UsernameAtRun == "Driver One");
        Assert.Equal(CellStatus.Finished, one.Cells[0].Status);
        Assert.Equal(CellStatus.Dnf, one.Cells[1].Status);
        Assert.Equal([98410L], one.Cells[1].SectorsMs);
        var three = snapshot.Drivers.Single(d => d.UsernameAtRun == "Driver Three");
        Assert.Equal(CellStatus.NotStarted, three.Cells[1].Status);
    }

    [Fact]
    public void TimeIsSectorsPlusPenaltyExactly()
    {
        var snapshot = Fixtures.Board().BuildSubmission(0);
        foreach (var cell in snapshot.Drivers.SelectMany(d => d.Cells).Where(c => c.Status == CellStatus.Finished))
            Assert.Equal(cell.TimeMs, cell.SectorsMs.Sum() + cell.PenaltyMs);
        var two = snapshot.Drivers.Single(d => d.UsernameAtRun == "Driver Two").Cells[0];
        Assert.Equal(10000, two.PenaltyMs);
        Assert.Equal(470002, two.TimeMs);
        Assert.Equal("Hyundai i20 Rally2", two.CarName);
        Assert.Equal("Hyundaii20NRally2", two.CarId);
    }

    [Fact]
    public void UnconfirmedFinishBlocks()
    {
        var board = Fixtures.Board();
        board.AddColumn("run3", "SS3 AlsaceS4SaverneFullForward");
        board.AddResult("run3", "Driver One", 400, 400, 3, [100, 200, 400], [100, 100, 200]);
        var snapshot = board.BuildSubmission(0);
        Assert.Contains(snapshot.Problems, p => p.Contains("Driver One"));
    }

    [Fact]
    public void RunningStageBlocks()
    {
        var board = Fixtures.Board();
        board.SetLobbyPhase("Racing");
        Assert.Contains(board.BuildSubmission(0).Problems, p => p.Contains("still running"));
    }

    [Fact]
    public void RallyTakesAllItsStagesTickedOrNot()
    {
        var board = Fixtures.Board();
        board.GroupStages(["run1", "run2"], "Summer Rally");
        board.SetDiscarded("run2", true);
        Assert.Equal(2, board.BuildSubmission(1).Stages.Count);
        Assert.Single(board.BuildSubmission(0).Stages);   // All stages: the ticked ones
    }

    [Fact]
    public void GroupSelectionHasItsOwnRunId()
    {
        var board = Fixtures.Board();
        board.GroupStages(["run2"], "Summer Rally");
        var all = board.BuildSubmission(0);
        var rally = board.BuildSubmission(1);
        Assert.NotEqual(all.RunId, rally.RunId);
        Assert.Equal(rally.RunId, board.BuildSubmission(1).RunId);
        Assert.Single(rally.Stages);
        Assert.Equal("Summer Rally", rally.GroupName);
        Assert.DoesNotContain(rally.Drivers, d => d.UsernameAtRun == "Driver Three");
    }

    [Fact]
    public void DestinationPayload()
    {
        var snapshot = Fixtures.Board().BuildSubmission(0);
        var root = Doc(ResultsPayload.ForDestination(snapshot, Destination(pairedBefore: true), new DateTime(2026, 8, 15, 20, 42, 12, 418, DateTimeKind.Utc)));
        Assert.Equal("acrlt-results/1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("summer-r03", root.GetProperty("eventId").GetString());
        Assert.Equal("operator-confirmed", root.GetProperty("submissionMode").GetString());
        Assert.Equal("2026-08-15T20:42:12.418Z", root.GetProperty("generatedAt").GetString());
        Assert.False(root.TryGetProperty("revision", out _));
        Assert.False(root.TryGetProperty("sourceMessageId", out _));
        var stage = root.GetProperty("stages")[0];
        Assert.True(stage.GetProperty("startedAfterPairing").GetBoolean());
        Assert.True(stage.TryGetProperty("startedAt", out _));

        var two = root.GetProperty("participants").EnumerateArray().Single(p => p.GetProperty("usernameAtRun").GetString() == "Driver Two");
        Assert.Equal(Fixtures.SteamTwo, two.GetProperty("steamId64").GetString());
        Assert.Equal("SwitzerlAnd", two.GetProperty("nation").GetString());
        Assert.Equal("Switzerland", two.GetProperty("nationName").GetString());
        var cell = two.GetProperty("stages")[0];
        Assert.Equal("finished", cell.GetProperty("status").GetString());
        Assert.Equal(470002, cell.GetProperty("timeMs").GetInt64());
        Assert.False(cell.TryGetProperty("splitsMs", out _));
    }

    [Fact]
    public void LatePairingIsFlagged()
    {
        var snapshot = Fixtures.Board().BuildSubmission(0);
        var root = Doc(ResultsPayload.ForDestination(snapshot, Destination(pairedBefore: false), DateTime.UtcNow));
        Assert.False(root.GetProperty("stages")[0].GetProperty("startedAfterPairing").GetBoolean());
    }

    [Fact]
    public void FileExportHasNoDestinationFields()
    {
        var root = Doc(ResultsPayload.ForFile(Fixtures.Board().BuildSubmission(0), DateTime.UtcNow));
        Assert.Equal("file-export", root.GetProperty("submissionMode").GetString());
        Assert.False(root.TryGetProperty("eventId", out _));
        Assert.False(root.GetProperty("stages")[0].TryGetProperty("startedAfterPairing", out _));
    }
}
