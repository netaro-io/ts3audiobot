# Hostleaf-Fork

Fork von [Splamy/TS3AudioBot](https://github.com/Splamy/TS3AudioBot), abgezweigt bei
`142e4e2` (upstream `develop`, 24.08.2026). Der Upstream bleibt als Remote `origin`
erhalten, dieser Fork liegt im Branch `hostleaf-audio`.

Das Original-README steht in `README.md`. Dieses hier beschreibt nur, was der Fork ändert.

## Audio-Kern

Der Ringpuffer ist lock-frei. Vorher hielt der Pump-Thread die Sperre des Ringpuffers
während `Monitor.Wait` innerhalb der Schleife, sodass er bei vollem FIFO - dem Regelfall -
mit dem Audio-Thread um dieselbe Sperre kämpfte. Das war als Stocken hörbar.

`PassiveMergePipe` ließ alle Producer in denselben Puffer lesen und addierte danach auf.
Lieferte ein Producer weniger Bytes als der vorige, blieben dahinter dessen Samples stehen
und wurden ein zweites Mal addiert - doppelte Amplitude in Teilblöcken, hart abgeschnitten.
Hörbar als Knacken bei jedem Überblendstart. Jeder Producer liest jetzt in seinen eigenen
Puffer, und nur der tatsächlich gelesene Bereich wird gemischt.

Dazu: Equal-Power-Rampen statt linearer Interpolation zwischen zwei Lautstärken,
asynchroner Crossfade, ein Shuffle-Bag ohne Seiteneffekte beim Nachfüllen, Opus im
VBR-Modus und ein behobener `Math.Abs(short.MinValue)`-Überlauf in `PreciseTimedPipe`.

## Avatar-Karte

`TS3AudioBot/NowPlayingCard.cs` (neu) rendert Cover, Titel, Künstler und einen
Fortschrittsbalken als Client-Avatar, aktualisiert im 20-Sekunden-Raster. Cover kommen von
Deezer, iTunes, dem Cover Art Archive oder dem Video-Thumbnail, mit Cache.

Die Treffer-Prüfung ist bewusst streng: ein fremdes Cover ist schlechter als der generierte
Ersatz, und die Anbieter liefern auf jede Anfrage irgendetwas. Abweichende Künstlernamen
werden nur akzeptiert, wenn der Künstler Teil der Suchanfrage war und beide Namen in
verschiedenen Schriftsystemen geschrieben sind - sonst bekäme das japanische "Lemon" das
Cover von U2 und "Magnum Opus" eines von Kollegah.

Schriften fallen auf Noto CJK zurück, damit japanische oder kyrillische Namen nicht als
leere Kästen erscheinen.

## Ressourcen

Workstation-GC statt Server-GC: 315 -> 197 MB im Leerlauf, vier Threads weniger. Server-GC
legt pro Kern einen Heap an, was für einen Bot bei nahezu null CPU-Last nichts bringt, und
seine längeren Stop-the-World-Pausen sind für einen Audio-Thread das Falsche. Dazu
`RetainVM=false`, ein Cover- und Logo-Cache und ein wiederverwendeter JPEG-Puffer.

`InvariantGlobalization` ist bewusst **nicht** gesetzt: es würde `string.Normalize()`
werfen lassen, worauf der Cover-Abgleich beruht.

## Betrieb

Läuft als systemd-Dienst gegen die Standardpfade (`/opt/ts3audiobot/app` für die DLLs,
`/var/lib/ts3audiobot` für Daten und Konfiguration). Bauen und ausrollen:

```bash
dotnet build TS3AudioBot/TS3AudioBot.csproj -c Release
cp TS3AudioBot/bin/Release/net10.0/TS3AudioBot.dll /opt/ts3audiobot/app/
systemctl restart ts3audiobot
```

Damit der Bot nach einem Neustart von allein weiterspielt, startet `onconnect` in der
`bot.toml` die Playlist samt Zufallswiedergabe und Dauerschleife:

```toml
onconnect = '!xecute (!random on) (!repeat all) (!list play radio)'
```

## Lizenz

Upstream steht unter **OSL 3.0** (siehe `LICENSE`), dieser Fork ebenso. Die
External-Deployment-Klausel wertet den Betrieb als Dienst für Dritte als Weitergabe - die
Änderungen sind deshalb hier öffentlich einsehbar.
