using LouisaWiFi.Windows.Models;
using LouisaWiFi.Windows.Platform;
using LouisaWiFi.Windows.Services;

namespace LouisaWiFi.Windows.Tests;

public class ConnectionPlannerTests
{
    private sealed class Secrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string bssid) => Values.GetValueOrDefault(LearnedStore.Normalize(bssid));
        public void Set(string bssid, string password) => Values[LearnedStore.Normalize(bssid)] = password;
    }

    private static Store StoreWithPasswords(params string[] passwords) =>
        new("測試門市", "台北市", "", 25, 121, passwords);

    [Fact]
    public void LearnedPasswordIsTriedBeforeCandidatesAndDuplicatesAreRemoved()
    {
        var store = StoreWithPasswords("candidate-a", "candidate-b");
        var scan = new WifiScan([
            new WifiAccessPoint("AA:BB:CC:DD:EE:01", -50),
            new WifiAccessPoint("AA:BB:CC:DD:EE:02", -60)
        ]);
        IReadOnlyDictionary<string, LearnedEntry> learned = new Dictionary<string, LearnedEntry>
        {
            ["aa:bb:cc:dd:ee:01"] = new("測試門市", 1),
            ["aa:bb:cc:dd:ee:02"] = new("測試門市", 0)
        };

        var planned = ConnectionPlanner.Plan(store, scan, learned, new Secrets());

        Assert.Equal(["candidate-b", "candidate-a"], planned.Select(p => p.Password));
        Assert.Equal([1, 0], planned.Select(p => p.PasswordIndex));
    }

    [Fact]
    public void CustomLearnedPasswordPrecedesCandidates()
    {
        var store = StoreWithPasswords("candidate");
        var scan = new WifiScan([new WifiAccessPoint("AA:BB:CC:DD:EE:01", -50)]);
        IReadOnlyDictionary<string, LearnedEntry> learned = new Dictionary<string, LearnedEntry>
        {
            ["aa:bb:cc:dd:ee:01"] = new("測試門市", null)
        };
        var secrets = new Secrets();
        secrets.Set("AA:BB:CC:DD:EE:01", "manual-pass");

        var planned = ConnectionPlanner.Plan(store, scan, learned, secrets);

        Assert.Equal(["manual-pass", "candidate"], planned.Select(p => p.Password));
        Assert.Null(planned[0].PasswordIndex);
    }

    [Fact]
    public void ExplicitPasswordIsTheOnlyAttempt()
    {
        var planned = ConnectionPlanner.Plan(
            StoreWithPasswords("candidate"),
            new WifiScan([]),
            new Dictionary<string, LearnedEntry>(),
            new Secrets(),
            "manual-pass");

        Assert.Equal([new PasswordCandidate("manual-pass", null)], planned);
    }
}
