# MarketWatcher

MarketWatcher ist ein lokales Windows-Privatprojekt zum Beobachten selbst angelegter Such-URLs auf Gebrauchtwarenplattformen. Die Anwendung merkt sich bekannte Anzeigen und kann neue Treffer per E-Mail, Telegram oder WhatsApp Web melden.

## Funktionen

- WPF-Oberfläche, Tray-Betrieb und optionaler Windows-Autostart
- konfigurierbares Prüfintervall und lokale Ausschlussliste
- automatische Erkennung fertiger Such-URLs und ihrer enthaltenen Filter
- unterstützte Plattformen: Willhaben, Kleinanzeigen, Vinted, eBay.at, eBay.de, markt.de, Quoka und Tutti
- Suchen können pausiert und wahlweise mit oder ohne Nachmeldung verpasster Treffer fortgesetzt werden
- mehrere WhatsApp- und Telegram-Empfänger mit Zuordnung je Suche
- WhatsApp-Web-Anmeldung über separates Edge-Profil und QR-Code
- Edge wird nach jedem Such- und Versandlauf geschlossen
- lokale Geheimnisse werden mit Windows DPAPI geschützt

## Bauen

Voraussetzungen: Windows, .NET 10 SDK und Microsoft Edge.

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
```

## Datenschutz

Konfiguration, Suchen und Ausschlussliste liegen im lesbaren JSON-Format unter `data\state.json` direkt neben der Anwendung. Eine Sicherung liegt unter `data\backups\state-latest.json`. Das Edge-Profil befindet sich ebenfalls im `data`-Ordner. Damit kann der gesamte Programmordner gemeinsam kopiert werden; eine SQLite-Datenbank wird nicht verwendet.

Beim Start erkennt MarketWatcher automatisch einen anderen Computer oder Windows-Benutzer. Suchen und Ausschlussliste bleiben erhalten. Nicht übertragbare, benutzergebunden verschlüsselte Geheimnisse werden aus Sicherheitsgründen verworfen und die Anwendung fordert zur erneuten Eingabe beziehungsweise WhatsApp-QR-Anmeldung auf.

## Rechtlicher Hinweis

Dies ist ein inoffizielles, nicht kommerzielles Privatprojekt. Es besteht keine Verbindung zu Willhaben, Kleinanzeigen, Vinted, eBay, WhatsApp, Telegram, Microsoft oder Google. Produkt- und Markennamen gehören ihren jeweiligen Inhabern.

Die Software darf nur für rechtmäßige, persönliche Zwecke und unter Einhaltung der Nutzungsbedingungen, Zugriffsbeschränkungen, Datenschutzvorgaben und Automatisierungsregeln der verwendeten Plattformen eingesetzt werden. Nutzer sind selbst dafür verantwortlich, zulässige Abfrageintervalle zu wählen und Konten, personenbezogene Daten sowie Zugangsdaten zu schützen. Die Software ist nicht dazu vorgesehen, CAPTCHAs, Zugriffsschutz, Rate-Limits oder andere Schutzmaßnahmen zu umgehen.

Die Software wird ohne Gewährleistung bereitgestellt. Es wird keine Haftung für Kontosperren, Datenverlust, verpasste oder fehlerhafte Benachrichtigungen, Schäden oder sonstige Folgen übernommen, soweit gesetzlich zulässig. Dies ist keine Rechtsberatung.

## Lizenz

Siehe [LICENSE](LICENSE).
