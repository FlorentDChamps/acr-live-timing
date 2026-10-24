using System.IO;
using System.Text.Json;
using ACRLiveTiming.Model;
using ACRLiveTiming.Net;
using ACRLiveTiming.Session;
using ACRLiveTiming.Submission;
using Xunit;

namespace ACRLiveTiming.Tests;

public sealed class SessionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "acrlt-tests-" + Guid.NewGuid().ToString("N"));

    public SessionTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static readonly DateTime At = new(2026, 10, 23, 20, 14, 5, 123, DateTimeKind.Utc);

    [Fact]
    public void ADatagramWrittenIsReadBackWithItsTime()
    {
        string path = Path.Combine(_folder, "a.pcap");
        using (var writer = new PcapWriter(path))
        {
            writer.WriteUdp("203.0.113.10", 7777, [1, 2, 3, 4, 5], At.AddTicks(4560));
            writer.WriteUdp("203.0.113.10", 7777, [9], At.AddSeconds(1));
        }
        var packets = PcapReplay.ReadUdp(path).ToList();
        Assert.Equal(2, packets.Count);
        Assert.Equal(("203.0.113.10", 7777), (packets[0].ip, packets[0].port));
        Assert.Equal([1, 2, 3, 4, 5], packets[0].payload);
        Assert.Equal(At.AddTicks(4560), packets[0].utc);   // µs precision
        Assert.Equal(DateTimeKind.Utc, packets[0].utc.Kind);
        Assert.Equal(At.AddSeconds(1), packets[1].utc);
        Assert.Null(PcapReplay.CheckHeader(path));
    }

    [Fact]
    public void AnAppendCutsAPartialLastRecord()
    {
        string path = Path.Combine(_folder, "b.pcap");
        using (var writer = new PcapWriter(path))
        {
            writer.WriteUdp("203.0.113.10", 7777, [1], At);
            writer.WriteUdp("203.0.113.10", 7777, [2, 2], At);
        }
        using (var fs = new FileStream(path, FileMode.Open)) fs.SetLength(fs.Length - 3);   // a crash mid-write
        using (var writer = PcapWriter.Append(path))
        {
            Assert.Equal(1, writer.Count);
            writer.WriteUdp("203.0.113.10", 7777, [3, 3, 3], At);
        }
        var payloads = PcapReplay.ReadUdp(path).Select(p => p.payload).ToList();
        Assert.Equal([[1], [3, 3, 3]], payloads);
    }

    [Fact]
    public void KeepsOnlyTheMostRecentAutomaticSessions()
    {
        for (int i = 1; i <= 4; i++)
        {
            string stem = Path.Combine(_folder, $"session_20261001_20000{i}");
            File.WriteAllText(stem + ".pcap", "");
            File.WriteAllText(stem + ".json", "{}");
            File.SetLastWriteTimeUtc(stem + ".pcap", At.AddDays(-10 + i));
            File.SetLastWriteTimeUtc(stem + ".json", At.AddDays(-10 + i));
        }
        File.WriteAllText(Path.Combine(_folder, "Final round.pcap"), "");
        File.SetLastWriteTimeUtc(Path.Combine(_folder, "Final round.pcap"), At.AddDays(-30));

        var journal = new SessionJournal(_folder, () => 3, () => new SessionSettings(), _ => { });
        journal.Write("203.0.113.10", 7777, [1], At);   // opens a new session: 3 kept with it
        journal.Close();

        var names = Directory.GetFiles(_folder).Select(Path.GetFileName).ToHashSet();
        Assert.Contains("Final round.pcap", names);
        Assert.Contains("session_20261001_200004.pcap", names);
        Assert.Contains("session_20261001_200003.json", names);
        Assert.DoesNotContain("session_20261001_200002.pcap", names);
        Assert.DoesNotContain("session_20261001_200001.json", names);
        Assert.Equal(7, names.Count);   // new pair + 2 kept pairs + the renamed one
    }

    [Fact]
    public void TheOrganisersChoicesComeBack()
    {
        var board = Fixtures.Board();
        board.SetDiscarded("run2", true);
        board.GroupStages(["run1", "run2"], "Rally Alsace");
        var saved = board.ExportSettings();

        var restored = Fixtures.Board();
        restored.ApplySettings(saved);
        Assert.Equal(board.BuildSubmission(1).RunId, restored.BuildSubmission(1).RunId);
        Assert.Equal(board.BuildSubmission(0).RunId, restored.BuildSubmission(0).RunId);
        Assert.Equal("Rally Alsace", restored.BuildSubmission(1).GroupName);
        Assert.Contains(restored.BuildView().AllStages, s => s.Id == "run2" && s.Discarded);
    }

    [Fact]
    public void AResetKeepsTheSessionAndGoesOnInACopy()
    {
        var board = Fixtures.Board();
        var journal = new SessionJournal(_folder, () => 5, board.ExportSettings, _ => { });
        journal.Write("203.0.113.10", 7777, [1], At);
        journal.Write("203.0.113.10", 7777, [2], At);
        board.GroupStages(["run1"], "Rally Alsace");
        journal.SaveSettings();
        string before = journal.CurrentPath!;

        board.ResetKeepIdentity();
        journal.OnReset();
        journal.Write("203.0.113.10", 7777, [3], At);
        string after = journal.CurrentPath!;
        journal.Close();

        Assert.NotEqual(before, after);
        Assert.Equal([[1], [2]], PcapReplay.ReadUdp(before).Select(p => p.payload).ToList());
        var kept = SessionJournal.ReadSettings(before)!;
        Assert.Empty(kept.Resets);
        Assert.Equal("Rally Alsace", kept.Settings.GroupNames[1]);   // as it was before the reset

        Assert.Equal([[1], [2], [3]], PcapReplay.ReadUdp(after).Select(p => p.payload).ToList());
        var next = SessionJournal.ReadSettings(after)!;
        Assert.Equal([2L], next.Resets);
        Assert.Empty(next.Settings.GroupNames);
    }

    [Fact]
    public void AResetWhileLoadingIsOnlyNoted()
    {
        var journal = new SessionJournal(_folder, () => 5, () => new SessionSettings(), _ => { });
        journal.Write("203.0.113.10", 7777, [1], At);
        string path = journal.CurrentPath!;
        journal.OnReset(split: false);
        Assert.Equal(path, journal.CurrentPath);
        journal.Close();
        Assert.Equal([1L], SessionJournal.ReadSettings(path)!.Resets);
    }

    [Fact]
    public void PairingAndPacketTimesCompareInUtc()
    {
        // the pairing time as stored (an offset of its own) and read back
        var paired = new Association { PairedAt = DateTimeOffset.UtcNow };
        string stored = JsonSerializer.Serialize(paired);
        var restored = JsonSerializer.Deserialize<Association>(stored)!;
        Assert.Equal(paired.PairedAt.UtcDateTime, restored.PairedAt.UtcDateTime);

        // the same instant written with another offset still compares as that instant
        var elsewhere = new Association { PairedAt = new DateTimeOffset(At).ToOffset(TimeSpan.FromHours(2)) };
        Assert.True(elsewhere.StartedAfterPairing(new SnapshotStage { StartedAtUtc = At.AddSeconds(1) }));
        Assert.False(elsewhere.StartedAfterPairing(new SnapshotStage { StartedAtUtc = At.AddSeconds(-1) }));

        // a start given in local time is taken to UTC before comparing
        var local = At.AddSeconds(1).ToLocalTime();
        Assert.True(elsewhere.StartedAfterPairing(new SnapshotStage { StartedAtUtc = local }));

        // a packet time written then read back is the same UTC instant
        string path = Path.Combine(_folder, "t.pcap");
        using (var writer = new PcapWriter(path)) writer.WriteUdp("203.0.113.10", 7777, [1], At.ToLocalTime());
        Assert.Equal(At, PcapReplay.ReadUdp(path).Single().utc);
    }

    [Fact]
    public void TimesAreSentInUtc()
    {
        var snapshot = new ResultsSnapshot
        {
            RunId = "r",
            Stages = [new SnapshotStage { ColumnId = "run1", Name = "SS1", StartedAtUtc = At.ToLocalTime() }]
        };
        var doc = JsonDocument.Parse(ResultsPayload.ForFile(snapshot, At.AddMinutes(30).ToLocalTime())).RootElement;
        Assert.Equal("2026-10-23T20:44:05.123Z", doc.GetProperty("generatedAt").GetString());
        Assert.Equal("2026-10-23T20:14:05.123Z", doc.GetProperty("stages")[0].GetProperty("startedAt").GetString());
    }

    [Fact]
    public void AStageStartsWhenItsPacketArrived()
    {
        var board = new SessionMatrix();
        board.SetPacketTime(At, recorded: false);   // live, decoded a few seconds after arriving
        board.AddColumn("run1", "SS1 AlsaceS4SaverneFullForward");
        Fixtures.Finish(board, "run1", "Driver One", [151.0, 302.1, 452.8], 0);
        var stage = board.BuildSubmission(0).Stages.Single();
        Assert.Equal(At, stage.StartedAtUtc);
        Assert.NotNull(stage.StartTimestamp);
    }

    [Fact]
    public void ALoadedStageIsComparedOnItsRecordedTime()
    {
        var paired = new Association { PairedAt = new DateTimeOffset(At), PairedTimestamp = long.MaxValue };
        Assert.True(paired.StartedAfterPairing(new SnapshotStage { StartedAtUtc = At.AddMinutes(5) }));
        Assert.False(paired.StartedAfterPairing(new SnapshotStage { StartedAtUtc = At.AddMinutes(-5) }));
        Assert.False(paired.StartedAfterPairing(new SnapshotStage()));
        // a start seen live keeps the monotonic rule
        Assert.False(paired.StartedAfterPairing(new SnapshotStage { StartTimestamp = 1, StartedAtUtc = At.AddMinutes(5) }));
    }
}
