namespace AionDPS.Aion2;

/// <summary>Varlık (assets/aion2) klasörünün kökü. Motor kurucusu ayarlar; tablolar ilk kullanımda buradan okunur.</summary>
public static class AssetPaths
{
    public static string Root { get; set; } = Path.Combine(AppContext.BaseDirectory, "assets", "aion2");

    public static string Of(params string[] parts) => Path.Combine([Root, .. parts]);
}
