using System.Reflection;

namespace AIT.TermCatalog;

/// <summary>
/// Symbole der Katalog-Ebenen: Sachgebiet = Bücherregal, Hauptgruppe = Aktenschrank, Gruppe = Aktenordner, Untergruppe = Bücherstapel
/// (Begriff behält sein eigenes Symbol). Je Ebene ein Navigations-Symbol und je ein Menü-Symbol für „Neu“ und „Bearbeiten“,
/// als ICO mit mehreren Größen (16 bis 256 Pixel).
/// </summary>
public static class TermIcons
{
    /// <summary>Navigations-Symbol der Ebene (0 = Sachgebiet … 3 = Untergruppe) als ICO-Bytes; null bei ungültiger Ebene.</summary>
    public static byte[]? Level(int level) => level is >= 0 and <= 3 ? Read($"Ebene{level + 1}.ico") : null;

    /// <summary>Menü-Symbol der Ebene (0 … 3) für „Neu“ (add) bzw. „Bearbeiten“.</summary>
    public static byte[]? Menu(int level, bool add) => level is >= 0 and <= 3 ? Read($"Ebene{level + 1}-{(add ? "add" : "edit")}.ico") : null;

    /// <summary>Alle Symbole mit ihren Dateinamen (Ebene1.ico, Ebene1-add.ico, Ebene1-edit.ico, … Ebene4-edit.ico).</summary>
    public static IEnumerable<(string FileName, byte[] Data)> All()
    {
        for (var level = 0; level < 4; level++)
            foreach (var suffix in new[] { "", "-add", "-edit" })
            {
                var name = $"Ebene{level + 1}{suffix}.ico";
                var data = Read(name);
                if (data != null) yield return (name, data);
            }
    }

    private static byte[]? Read(string fileName)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("AIT.TermCatalog.Icons." + fileName);
        if (s == null) return null;
        using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
    }
}