using System.Text;

namespace AIT.TermCatalog;

/// <summary>Dateizugriff für die Patch-Dienste: Zeilenumbruch-Erkennung und robustes Schreiben in geschützte ProgramData-Ordner.</summary>
public static class FileIo
{
    /// <summary>Zeilenumbruch einer Textdatei nach Mehrheit (nicht "enthält irgendwo CRLF"): schon ein verirrtes
    /// CRLF in einer LF-Datei darf nicht dazu führen, dass zeilenweise Operationen riesige Blöcke erfassen.</summary>
    public static string DetectNewLine(string text)
    {
        var crlf = 0; var idx = 0;
        while ((idx = text.IndexOf("\r\n", idx, StringComparison.Ordinal)) >= 0) { crlf++; idx += 2; }
        var lf = 0; idx = 0;
        while ((idx = text.IndexOf('\n', idx)) >= 0) { lf++; idx++; }
        return crlf > 0 && crlf * 2 >= lf ? "\r\n" : "\n";
    }

    /// <summary>
    /// Schreibt <paramref name="content"/> in <paramref name="path"/> mit dreistufigem Fallback:
    /// <list type="number">
    ///   <item>Direktes Überschreiben (<see cref="File.WriteAllText"/>)</item>
    ///   <item>Read-only-Attribut entfernen, dann nochmals versuchen</item>
    ///   <item>Datei löschen + neu anlegen (umgeht datei-spezifische Write-ACLs
    ///         wenn das Verzeichnis "Delete subfolders and files" + "Create files" erlaubt)</item>
    /// </list>
    /// Wirft bei echtem Berechtigungsproblem nach Ausschöpfung aller Fallbacks weiterhin
    /// <see cref="UnauthorizedAccessException"/> – der Aufrufer muss es fangen.
    /// </summary>
    public static void WriteTextRobust(string path, string content, Encoding encoding)
    {
        // ── Versuch 1: direkt ─────────────────────────────────────────────
        try
        {
            File.WriteAllText(path, content, encoding);
            return;
        }
        catch (UnauthorizedAccessException) { /* → Versuch 2 */ }

        // ── Versuch 2: Read-only-Attribut entfernen ───────────────────────
        try
        {
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);

            File.WriteAllText(path, content, encoding);
            return;
        }
        catch (UnauthorizedAccessException) { /* → Versuch 3 */ }
        catch { throw; } // andere Fehler (IOException etc.) direkt weiterwerfen

        // ── Versuch 3: Datei löschen + neu anlegen ────────────────────────
        // Klappt wenn das Verzeichnis "Delete subfolders and files" + "Create files"
        // erlaubt, die Datei-ACL aber Write verweigert (typisch nach Autodesk-Install).
        // Ein Backup sollte VOR diesem Aufruf bereits angelegt worden sein.
        File.Delete(path);
        File.WriteAllText(path, content, encoding);
    }
}
