using ACRLiveTiming.Model;

namespace ACRLiveTiming.Tests;

/// <summary>A small board: two identified drivers and one without identity on
/// two stages, filled through the same calls the engine makes.</summary>
internal static class Fixtures
{
    public const string SteamOne = "76561190000000001";
    public const string EosOne = "0002aaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string SteamTwo = "76561190000000002";
    public const string EosTwo = "0002bbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public static SessionMatrix Board()
    {
        var m = new SessionMatrix();
        m.SetDriverIdentity("Driver One", SteamOne, EosOne);
        m.SetDriverIdentity("Driver Two", SteamTwo, EosTwo);
        m.SetDriverInfo(null, "Driver One", "France", null);
        m.SetDriverInfo(null, "Driver Two", "SwitzerlAnd", null);

        // SS1: One finishes clean, Two finishes with a 10 s penalty, Three
        // (never identified) finishes.
        m.AddColumn("run1", "SS1 AlsaceS4SaverneFullForward");
        m.SetDriverCar("run1", "Driver One", "SkodaFabiaRSRally2");
        m.SetDriverCar("run1", "Driver Two", "Hyundaii20NRally2");
        Finish(m, "run1", "Driver One", [151.0344, 302.1121, 452.8712], 0);
        Finish(m, "run1", "Driver Two", [153.3, 306.7004, 460.0021], 10);
        Finish(m, "run1", "Driver Three", [155.5, 311.0, 466.5], 0);

        // SS2: One retires after the first split, Two finishes, Three never starts.
        m.AddColumn("run2", "SS2 WelesS3HafrenNorthFullForward");
        m.AddResult("run2", "Driver One", 98.4104, 98.4104, 1, [98.4104], [98.4104]);
        m.MarkDriverDnf("run2", "Driver One");
        Finish(m, "run2", "Driver Two", [100.0, 200.0, 300.0], 0);
        return m;
    }

    public static void Finish(SessionMatrix m, string column, string driver, double[] splits, double penalty)
    {
        double raw = splits[^1];
        var sectors = splits.Select((v, i) => (double?)(i == 0 ? v : v - splits[i - 1])).ToList();
        m.AddResult(column, driver, raw + penalty, raw, splits.Length, splits, sectors);
        m.MarkDriverFinished(column, driver, raw);
    }
}
