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

## Stoppen ohne Knacken

`Player.Stop` fadete die Lautstärke auf null und hielt danach sofort den Sendethread an
(`PreciseTimedPipe.Paused`). Hörbar war das als Knacken, und zwar aus drei Gründen
gleichzeitig: Opus arbeitet mit Overlap-Add, der letzte Frame lässt sich ohne seinen
Nachfolger nicht auflösen; der Jitterpuffer des Clients lief ohne Vorwarnung leer, worauf
dieser den Abriss extrapoliert statt in Stille zu gehen; und der Fade endete im Puffer,
nicht beim Hörer.

Nach dem Fade läuft jetzt eine kurze Nachlaufphase: ein Producer, der Stille liefert,
hängt für rund 120 ms in der `MergePipe`, und erst danach wird pausiert und die Quelle
abgebaut. Die Stille geht bewusst in die `MergePipe` und nicht an die Senke des Players -
die hängt hinter dem Encoder, dort wären rohe PCM-Nullen ein kaputtes Opus-Paket.

Dass die Quelle erst nach dem Nachlauf abgebaut wird, hat denselben Grund: ffmpeg zu
beenden, während der Tick-Thread noch liest, war die zweite Hälfte des Problems.

## Cover einzeln abrufbar

Die Avatar-Karte findet Cover schon, gibt aber nur das fertige Bild mit Text und
Fortschrittsbalken aus. Für eine Oberfläche, die den Titel selbst anzeigt, ist das zu
viel - gebraucht wird das nackte Cover.

`NowPlayingCard.CoverAsync` liefert es, und der Befehl `data song art get` gibt es als
JPEG heraus. Beides nutzt denselben Cache und dieselbe Trefferprüfung wie die Karte, damit
Oberfläche und Clientfenster nicht verschiedene Bilder zeigen.

Nicht zu verwechseln mit dem eingebauten `data song cover get`: das läuft über
`GetThumbnail` und verlangt einen `IThumbnailResolver` zum jeweiligen `AudioType`. Für
lokale Dateien und Radiostreams gibt es keinen, der Befehl liefert dort also nichts. Der
Weg über die Karte arbeitet dagegen mit dem Titelstring und greift auch bei Radio, wo der
Titel aus den ICY-Metadaten kommt.

Findet keine Quelle ein Cover, kommt der generierte Platzhalter - so bleibt immer ein Bild
übrig, und der Aufrufer braucht keinen Sonderfall.

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
