# AIT.TermCatalog

Gemeinsame Logik des **Benennungskatalogs (Terms)** im Autodesk Vault Data Standard – ohne Oberfläche, genutzt vom AIT-VDS-Konfigurator (und künftig vom AIT Vault-Katalog).

* `TermCatalogNames` – Namenstabelle der Custom Objects und Eigenschaften (Sachgebiet … Begriff, Begriff DE/EN/FR/IT; Vorlagen Deutsch/Englisch/Eigene).
* `TermCatalogNamesService` – stellt die Namen in MenuDefinitions.xml, Konfigurationsordnern, PS-Modulen, Skripten und Datenblättern um (idempotent, mit ZIP-Backup).
* `TermCopyService` / `TermCopyConfig` – Kopier-Zuordnung („Begriff übernehmen“, Titel-Sprache, weitere Eigenschaften).
* `TermCatalogPatcher` – Menüs, Titel-Buttons und Masken beibehalten bzw. aus dem Original wiederherstellen (`IsPresent`, `RunVault`, `RunCad`).
* `ChangeResult`, `FileIo` – Ergebnis eines Schritts und robustes Schreiben in geschützte ProgramData-Ordner.

Alle Schritte kennen eine Vorschau (`dryRun`) und legen vor jeder Änderung ein ZIP-Backup an. Die Klassifizierung von Dateien gehört bewusst nicht dazu.

## Verzeichnislayout
Die Repositories der AIT-Werkzeuge müssen als **Nachbarordner im selben Elternordner** liegen (relative Projektverweise):

```
<Elternordner>\AIT.WpfThemes
<Elternordner>\AIT.TermCatalog
<Elternordner>\AIT.VaultConnect
<Elternordner>\AIT-VDS-Konfigurator
<Elternordner>\AIT-Vault-Katalog
```
