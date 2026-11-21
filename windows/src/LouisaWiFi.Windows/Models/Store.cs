namespace LouisaWiFi.Windows.Models;

public sealed record Store(string Name, string Address, string Phone, double Lat, double Lng, IReadOnlyList<string> Passwords)
{
    public string City =>
        (Address.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "其他").Replace("臺", "台");

    /// stores.json uses 0,0 for stores without coordinates.
    public bool HasLocation => Lat != 0;
}
