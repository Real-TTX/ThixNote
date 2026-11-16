using ThixNote.Core.Infrastructure;

namespace ThixNote.Core.Business;

/// <summary>
/// A small library of made-up notes (two folders, Markdown, plain text, a protected note) for the snapshots of the README,
/// the UI tests and the self test – never real notes. Times are relative to <see cref="AppClock"/>, so a pinned clock gives
/// the same pictures every day. Only ever written into a test or temporary folder.
/// </summary>
public static class SampleNotes
{
    /// <summary>The password of the protected sample note.</summary>
    public const string Password = "demo";

    public const string Plan = @"Arbeit\Projektplan Q1.md";
    public const string Shopping = @"Privat\Einkaufsliste.txt";
    public const string Secret = "Zugangsdaten WLAN.txts";

    /// <summary>Writes the sample notes into <paramref name="folder"/> (created if needed) and returns their paths by their keys.</summary>
    public static IReadOnlyDictionary<string, string> Create(string folder)
    {
        var now = AppClock.Now.LocalDateTime;
        var paths = new Dictionary<string, string>();
        Directory.CreateDirectory(Path.Combine(folder, "Arbeit"));
        Directory.CreateDirectory(Path.Combine(folder, "Privat"));

        void Note(string relative, string content, double hoursAgo)
        {
            var path = Path.Combine(folder, relative.Replace('\\', Path.DirectorySeparatorChar));
            File.WriteAllText(path, content.Replace("\r\n", "\n"));
            File.SetLastWriteTime(path, now.AddHours(-hoursAgo));
            paths[relative] = path;
        }

        Note(Plan, """
            Ziel: **Version 2.0** bis Ende März ausliefern.

            ## Meilensteine

            | Woche | Thema | Status |
            |---|---|---|
            | 2 | Konzept abstimmen | erledigt |
            | 5 | Prototyp | in Arbeit |
            | 9 | Beta für das Team | offen |
            | 12 | Release | offen |

            ## Nächste Schritte

            - [x] Budget freigeben lassen
            - [x] Kick-off mit allen Beteiligten
            - [ ] Testplan schreiben
            - [ ] Release-Notes vorbereiten

            > Nächste Abstimmung: Donnerstag, 10 Uhr.
            """, 0.4);
        Note(@"Arbeit\Meeting 12.11.md", """
            - Feedback zum Prototyp sammeln
            - Termine für die Beta festlegen
            - *Aufgabe:* Folien bis Freitag
            """, 26);
        Note(@"Arbeit\Ideen Newsletter.md", """
            1. Rückblick auf das Quartal
            2. Tipps & Tricks
            3. Vorschau auf neue Funktionen
            """, 75);
        Note(Shopping, "Milch\nBrot\nÄpfel\nKaffee\nHaferflocken\nTomaten\n", 3);
        Note(@"Privat\Rezept Linsensuppe.md", """
            **Zutaten:** 250 g rote Linsen, 1 Zwiebel, 2 Karotten, 1 l Gemüsebrühe, Kreuzkümmel

            1. Zwiebel und Karotten anschwitzen
            2. Linsen und Brühe dazugeben, 20 Minuten köcheln
            3. Pürieren, mit Zitrone abschmecken
            """, 50);
        Note(@"Privat\Urlaub Norwegen.md", """
            - Fähre buchen
            - Regenjacke nicht vergessen
            - Route: Bergen → Geirangerfjord → Ålesund
            """, 140);
        Note("Lesestoff.md", "- Artikel über Markdown\n- Buchtipp von Anna\n", 200);

        var secret = Path.Combine(folder, Secret);
        File.WriteAllBytes(secret, NoteEncryption.Encrypt("WLAN Gäste\nKennwort: Sommer-2026\n", Password));
        File.SetLastWriteTime(secret, now.AddHours(-8));
        paths[Secret] = secret;
        return paths;
    }
}
