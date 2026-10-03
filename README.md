# MarktWächter

MarktWächter ist ein lokales Windows-Privatprojekt zum Beobachten selbst angelegter Such-URLs auf Gebrauchtwarenplattformen. Die Anwendung merkt sich bekannte Anzeigen und kann neue Treffer per E-Mail, Telegram oder WhatsApp Web melden.

## Funktionen

- WPF-Oberfläche, Tray-Betrieb und optionaler Windows-Autostart
- konfigurierbares Prüfintervall und lokale Ausschlussliste
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

Konfiguration, Ausschlussdatenbank und Browserprofil liegen unter `%LOCALAPPDATA%\MarktWaechter`. Diese Daten können Telefonnummern, Chat-IDs, E-Mail-Adressen, Sitzungen und verschlüsselte Zugangsdaten enthalten. Sie dürfen niemals eingecheckt oder weitergegeben werden.

## Rechtlicher Hinweis

Dies ist ein inoffizielles, nicht kommerzielles Privatprojekt. Es besteht keine Verbindung zu Willhaben, Kleinanzeigen, Vinted, eBay, WhatsApp, Telegram, Microsoft oder Google. Produkt- und Markennamen gehören ihren jeweiligen Inhabern.

Die Software darf nur für rechtmäßige, persönliche Zwecke und unter Einhaltung der Nutzungsbedingungen, Zugriffsbeschränkungen, Datenschutzvorgaben und Automatisierungsregeln der verwendeten Plattformen eingesetzt werden. Nutzer sind selbst dafür verantwortlich, zulässige Abfrageintervalle zu wählen und Konten, personenbezogene Daten sowie Zugangsdaten zu schützen. Die Software ist nicht dazu vorgesehen, CAPTCHAs, Zugriffsschutz, Rate-Limits oder andere Schutzmaßnahmen zu umgehen.

Die Software wird ohne Gewährleistung bereitgestellt. Es wird keine Haftung für Kontosperren, Datenverlust, verpasste oder fehlerhafte Benachrichtigungen, Schäden oder sonstige Folgen übernommen, soweit gesetzlich zulässig. Dies ist keine Rechtsberatung.

## Lizenz

Siehe [LICENSE](LICENSE).
