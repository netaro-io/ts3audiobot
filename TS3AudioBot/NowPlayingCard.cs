using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TS3AudioBot;

internal static partial class NowPlayingCard
{
	private const int Size = 400;
	private const float TextWidth = 360;
	private static readonly HttpClient Http = CreateHttp();
	private static readonly FontFamily TitleFamily = LoadFamily("Inter-SemiBold.ttf");
	private static readonly FontFamily TextFamily = LoadFamily("Inter-Regular.ttf");
	private static readonly FontFamily MarkFamily = LoadFamily("Inter-Medium.ttf");
	private const string InterPath = "/usr/local/share/fonts/inter/";
	private static readonly string LogoPath = "/var/lib/ts3audiobot/logo-wordmark.png";

	/// <summary>Height of the progress strip at the bottom edge, in pixels.</summary>
	private const int ProgressHeight = 6;

	/// <summary>Upper bound the TeamSpeak server accepts for an avatar.</summary>
	private const int AvatarSizeLimit = 110_000;
	private static readonly int[] JpegQualitySteps = [86, 78, 68, 56, 44];

	/// <summary>How many resolved covers to keep. Each is a 400x400 RGBA image, ~640 KB.</summary>
	private const int CoverCacheLimit = 48;
	private static readonly Dictionary<string, Image<Rgba32>?> CoverCache = [];
	private static readonly Queue<string> CacheOrder = new();
	private static readonly object CacheGate = new();

	[GeneratedRegex(@"\[[^\]]*\]")]
	private static partial Regex BracketTag { get; }

	[GeneratedRegex(@"\((?:official|prod\.?|audio|video|lyric|visualizer|hd|4k|remaster)[^)]*\)", RegexOptions.IgnoreCase)]
	private static partial Regex NoiseTag { get; }

	[GeneratedRegex(@"^(.+?)\s*\(([^)]+)\)$")]
	private static partial Regex TrailingArtist { get; }

	[GeneratedRegex(@"\s+")]
	private static partial Regex Spaces { get; }

	public readonly record struct Track(string Title, string Artist);

	public static Track Split(string? raw)
	{
		var text = raw ?? "";
		text = BracketTag.Replace(text, " ");
		text = NoiseTag.Replace(text, " ");
		text = Spaces.Replace(text, " ").Trim(' ', '-', '\u2013', '\u2014');
		var parts = text.Split(" - ", 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 2)
		{
			var artist = parts[0];
			var comma = artist.IndexOf(',');
			if (comma > 0)
				artist = artist[..comma].Trim();
			return new Track(parts[1], artist);
		}

		var paren = TrailingArtist.Match(text);
		if (paren.Success)
			return new Track(paren.Groups[1].Value.Trim(), paren.Groups[2].Value.Trim());

		return new Track(text, "");
	}

	/// <summary>
	/// Client description line: "Title · Artist · 1:23 / 4:56". Position and length are
	/// optional, so the line degrades gracefully for streams of unknown length.
	/// </summary>
	public static string Description(string? raw, bool playing, TimeSpan? position = null, TimeSpan? length = null)
	{
		if (!playing)
			return "";
		var track = Split(raw);
		if (string.IsNullOrWhiteSpace(track.Title))
			return "";

		var line = string.IsNullOrWhiteSpace(track.Artist)
			? track.Title
			: track.Title + " · " + track.Artist;

		var time = FormatTime(position, length);
		return time.Length == 0 ? line : line + " · " + time;
	}

	/// <summary>Formats elapsed and total time. A live stream has no length, then only the
	/// elapsed time is shown.</summary>
	public static string FormatTime(TimeSpan? position, TimeSpan? length)
	{
		if (position is null)
			return "";
		var elapsed = Clock(position.Value);
		if (length is null || length.Value <= TimeSpan.Zero)
			return elapsed;
		// A position past the end means the decoder read ahead; clamp so it never shows 4:01/4:00.
		var total = length.Value;
		if (position.Value > total)
			elapsed = Clock(total);
		return elapsed + " / " + Clock(total);
	}

	private static string Clock(TimeSpan value)
	{
		if (value < TimeSpan.Zero)
			value = TimeSpan.Zero;
		return value.TotalHours >= 1
			? ((int)value.TotalHours).ToString(CultureInfo.InvariantCulture) + value.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture)
			: ((int)value.TotalMinutes).ToString(CultureInfo.InvariantCulture) + value.ToString(@"\:ss", CultureInfo.InvariantCulture);
	}

	public static async Task<MemoryStream> RenderAsync(string? raw, string? audioType, string? resourceId, bool playing, CancellationToken cancellationToken)
		=> await RenderAsync(raw, audioType, resourceId, playing, null, cancellationToken);

	/// <summary>Renders the avatar card. <paramref name="progress"/> draws the position strip;
	/// pass null to leave it out (unknown length, live stream).</summary>
	public static async Task<MemoryStream> RenderAsync(string? raw, string? audioType, string? resourceId, bool playing, double? progress, CancellationToken cancellationToken)
	{
		if (!playing)
			return Idle();
		var track = Split(raw);
		using var cover = await LoadCoverAsync(track, resourceId, cancellationToken) ?? Poster(track);
		return Card(cover, track, progress);
	}

	/// <summary>Liefert nur das Cover des Titels, ohne die Karte darum.
	///
	/// Für das Kundenpanel: dort steht der Titel schon da, gebraucht wird das Bild.
	/// Nutzt denselben Cache und dieselbe Trefferprüfung wie die Avatar-Karte, damit
	/// Panel und Clientfenster nicht verschiedene Cover zeigen. Findet sich keins,
	/// kommt der generierte Platzhalter - so bleibt immer ein Bild übrig, und der
	/// Aufrufer braucht keinen Sonderfall.</summary>
	public static async Task<MemoryStream> CoverAsync(string? raw, string? resourceId, CancellationToken cancellationToken)
	{
		var track = Split(raw);
		using var cover = await LoadCoverAsync(track, resourceId, cancellationToken) ?? Poster(track);

		return Save(cover);
	}

	/// <summary>
	/// Resolves cover art, trying the cheapest and most reliable source first. Results are
	/// cached by track, including misses: the chain is up to six HTTP calls, and replaying it
	/// for a track that has no cover on every repeat is pure waste.
	/// </summary>
	private static async Task<Image<Rgba32>?> LoadCoverAsync(Track track, string? resourceId, CancellationToken cancellationToken)
	{
		var key = CacheKey(track, resourceId);
		lock (CacheGate)
		{
			if (CoverCache.TryGetValue(key, out var cached))
				return cached?.Clone();
		}

		var found = await ResolveCoverAsync(track, resourceId, cancellationToken);
		if (found is not null)
			found = Normalise(found);
		// The cache takes ownership of `found`; callers always get their own copy to dispose.
		return StoreAndCopy(key, found);
	}

	/// <summary>Crops the bars a source image may carry and scales it to the card format.
	/// Done once before caching, so a repeated track costs no image processing at all.</summary>
	private static Image<Rgba32> Normalise(Image<Rgba32> cover)
	{
		using (cover)
		{
			TrimLetterbox(cover);
			TrimFlatBars(cover);
			return FillFrame(cover);
		}
	}

	private static async Task<Image<Rgba32>?> ResolveCoverAsync(Track track, string? resourceId, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(track.Title))
			return await VideoThumbAsync(resourceId, cancellationToken);
		return await DeezerCoverAsync(track, true, cancellationToken)
			?? await ItunesCoverAsync(track, true, cancellationToken)
			?? await CoverArtArchiveAsync(track, cancellationToken)
			?? await DeezerCoverAsync(track, false, cancellationToken)
			?? await ItunesCoverAsync(track, false, cancellationToken)
			?? await VideoThumbAsync(resourceId, cancellationToken);
	}

	private static string CacheKey(Track track, string? resourceId)
		=> string.IsNullOrWhiteSpace(track.Title)
			? "id:" + (resourceId ?? "")
			: "t:" + Normalize(track.Title) + "|a:" + Normalize(track.Artist ?? "");

	/// <summary>Stores the resolved cover (or a null marker for a track that has none) and
	/// returns a private copy for the caller. Bounded and FIFO-evicted, so a long queue cannot
	/// grow the cache without limit.</summary>
	private static Image<Rgba32>? StoreAndCopy(string key, Image<Rgba32>? image)
	{
		lock (CacheGate)
		{
			if (CoverCache.TryGetValue(key, out var existing))
			{
				// Another render resolved the same track meanwhile - keep the stored one.
				image?.Dispose();
				return existing?.Clone();
			}
			if (CacheOrder.Count >= CoverCacheLimit)
			{
				var oldest = CacheOrder.Dequeue();
				if (CoverCache.Remove(oldest, out var evicted))
					evicted?.Dispose();
			}
			CoverCache[key] = image;
			CacheOrder.Enqueue(key);
			return image?.Clone();
		}
	}

	/// <summary>
	/// Generated cover for a track with no artwork anywhere. Deterministic per track, so the
	/// same song always looks the same, and distinct enough between tracks to be recognisable:
	/// a diagonal two-tone gradient plus the initial as a watermark.
	/// </summary>
	private static Image<Rgba32> Poster(Track track)
	{
		var seed = StableHash(Normalize(track.Title) + "|" + Normalize(track.Artist ?? ""));
		var hue = (int)(seed % 360);
		// Offset the second hue by a fixed angle so the gradient always has visible contrast.
		var hueB = (hue + 150 + (int)(seed / 360 % 60)) % 360;
		var from = Shade(ColorFromHue(hue), 0.42f);
		var to = Shade(ColorFromHue(hueB), 0.20f);

		var image = new Image<Rgba32>(Size, Size);
		image.Mutate(ctx => ctx.Fill(
			new LinearGradientBrush(
				new PointF(0, 0),
				new PointF(Size, Size),
				GradientRepetitionMode.None,
				new ColorStop(0f, from),
				new ColorStop(1f, to))));

		var initial = FirstLetter(track);
		if (initial.Length > 0)
		{
			var font = TitleFamily.CreateFont(210);
			var size = TextMeasurer.MeasureSize(initial, new TextOptions(font));
			image.Mutate(ctx => ctx.DrawText(
				initial,
				font,
				Color.FromRgba(255, 255, 255, 28),
				new PointF((Size - size.Width) / 2f, (Size - size.Height) / 2f - 20)));
		}
		return image;
	}

	/// <summary>FNV-1a. Unlike string.GetHashCode it is stable across processes, so a track keeps
	/// its colour after a restart.</summary>
	private static uint StableHash(string value)
	{
		var hash = 2166136261u;
		foreach (var character in value)
		{
			hash ^= character;
			hash *= 16777619u;
		}
		return hash;
	}

	private static Color Shade(Color color, float brightness)
	{
		var pixel = color.ToPixel<Rgba32>();
		return Color.FromRgb(
			(byte)(pixel.R * brightness),
			(byte)(pixel.G * brightness),
			(byte)(pixel.B * brightness));
	}

	private static string FirstLetter(Track track)
	{
		foreach (var source in new[] { track.Title, track.Artist })
		{
			if (string.IsNullOrWhiteSpace(source))
				continue;
			foreach (var character in source)
			{
				if (char.IsLetterOrDigit(character))
					return char.ToUpperInvariant(character).ToString();
			}
		}
		return "";
	}

	private static Color ColorFromHue(int hue)
	{
		var sector = hue / 60f;
		var index = (int)sector;
		var fraction = sector - index;
		var q = (byte)(255 * (1f - fraction));
		var t = (byte)(255 * fraction);
		return index switch
		{
			0 => Color.FromRgb(255, t, 0),
			1 => Color.FromRgb(q, 255, 0),
			2 => Color.FromRgb(0, 255, t),
			3 => Color.FromRgb(0, q, 255),
			4 => Color.FromRgb(t, 0, 255),
			_ => Color.FromRgb(255, 0, q),
		};
	}

	private static async Task<Image<Rgba32>?> VideoThumbAsync(string? resourceId, CancellationToken cancellationToken)
	{
		var name = System.IO.Path.GetFileNameWithoutExtension(resourceId ?? "");
		if (name.Length != 11)
			return null;
		foreach (var character in name)
		{
			if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')
				return null;
		}
		return await DownloadImageAsync($"https://i.ytimg.com/vi/{name}/hqdefault.jpg", cancellationToken);
	}

	private static async Task<Image<Rgba32>?> DeezerCoverAsync(Track track, bool requireArtist, CancellationToken cancellationToken)
	{
		var url = "https://api.deezer.com/search?limit=5&q=" + Uri.EscapeDataString(requireArtist ? Query(track) : track.Title);
		try
		{
			using var doc = await ReadJsonAsync(url, cancellationToken);
			if (doc is null || !doc.RootElement.TryGetProperty("data", out var data))
				return null;
			foreach (var hit in data.EnumerateArray())
			{
				var artist = hit.TryGetProperty("artist", out var artistEl) && artistEl.TryGetProperty("name", out var nameEl)
					? nameEl.GetString()
					: null;
				var title = hit.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
				if (!Matches(artist, title, track, requireArtist) || !hit.TryGetProperty("album", out var album))
					continue;
				var cover = album.TryGetProperty("cover_xl", out var xl) ? xl.GetString() : null;
				cover ??= album.TryGetProperty("cover_big", out var big) ? big.GetString() : null;
				if (!string.IsNullOrEmpty(cover))
					return await DownloadImageAsync(cover, cancellationToken);
			}
		}
		catch (Exception)
		{
			return null;
		}
		return null;
	}

	private static async Task<Image<Rgba32>?> ItunesCoverAsync(Track track, bool requireArtist, CancellationToken cancellationToken)
	{
		var url = "https://itunes.apple.com/search?entity=song&limit=5&term=" + Uri.EscapeDataString(requireArtist ? Query(track) : track.Title);
		try
		{
			using var doc = await ReadJsonAsync(url, cancellationToken);
			if (doc is null || !doc.RootElement.TryGetProperty("results", out var results))
				return null;
			foreach (var hit in results.EnumerateArray())
			{
				var artist = hit.TryGetProperty("artistName", out var artistEl) ? artistEl.GetString() : null;
				var title = hit.TryGetProperty("trackName", out var titleEl) ? titleEl.GetString() : null;
				var art = hit.TryGetProperty("artworkUrl100", out var artEl) ? artEl.GetString() : null;
				if (!Matches(artist, title, track, requireArtist) || string.IsNullOrEmpty(art))
					continue;
				return await DownloadImageAsync(art.Replace("100x100bb", "600x600bb", StringComparison.Ordinal), cancellationToken);
			}
		}
		catch (Exception)
		{
			return null;
		}
		return null;
	}

	private static async Task<Image<Rgba32>?> CoverArtArchiveAsync(Track track, CancellationToken cancellationToken)
	{
		var lucene = "recording:\"" + track.Title + "\"";
		if (!string.IsNullOrWhiteSpace(track.Artist))
			lucene += " AND artist:\"" + track.Artist + "\"";
		var url = "https://musicbrainz.org/ws/2/recording/?fmt=json&limit=3&query=" + Uri.EscapeDataString(lucene);
		try
		{
			using var doc = await ReadJsonAsync(url, cancellationToken);
			if (doc is null || !doc.RootElement.TryGetProperty("recordings", out var recordings))
				return null;
			foreach (var recording in recordings.EnumerateArray())
			{
				var title = recording.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
				var artist = FirstArtist(recording);
				if (!Matches(artist, title, track, true) || !recording.TryGetProperty("releases", out var releases))
					continue;
				foreach (var release in releases.EnumerateArray())
				{
					if (!release.TryGetProperty("id", out var idEl))
						continue;
					var releaseId = idEl.GetString();
					if (string.IsNullOrEmpty(releaseId))
						continue;
					var image = await DownloadImageAsync("https://coverartarchive.org/release/" + releaseId + "/front-500", cancellationToken);
					if (image is not null)
						return image;
				}
			}
		}
		catch (Exception)
		{
			return null;
		}
		return null;
	}

	private static async Task<JsonDocument?> ReadJsonAsync(string url, CancellationToken cancellationToken)
	{
		using var response = await Http.GetAsync(url, cancellationToken);
		if (!response.IsSuccessStatusCode)
			return null;
		await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
		return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
	}

	private static string Query(Track track)
		=> string.IsNullOrWhiteSpace(track.Artist) ? track.Title : track.Artist + " " + track.Title;

	private static string? FirstArtist(JsonElement recording)
	{
		if (!recording.TryGetProperty("artist-credit", out var credits))
			return null;
		foreach (var credit in credits.EnumerateArray())
		{
			if (credit.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } value)
				return value;
			if (credit.TryGetProperty("artist", out var artist) && artist.TryGetProperty("name", out var artistName))
				return artistName.GetString();
		}
		return null;
	}

	/// <summary>
	/// Decides whether a search hit really is the track being played. Deliberately strict: a
	/// wrong cover is worse than the generated fallback, and the providers happily return
	/// something for every query.
	/// </summary>
	private static bool Matches(string? foundArtist, string? foundTitle, Track track, bool requireArtist)
	{
		if (string.IsNullOrWhiteSpace(foundTitle))
			return false;

		var wantedTitle = Normalize(track.Title);
		var gotTitle = Normalize(foundTitle);
		if (wantedTitle.Length == 0 || gotTitle.Length == 0)
			return false;

		// Title must match closely. Containment alone is not enough: a short title is a
		// substring of countless unrelated songs.
		if (!TitleMatches(gotTitle, wantedTitle))
			return false;

		// With an artist known, it has to agree - that is the strongest signal there is.
		if (!string.IsNullOrWhiteSpace(track.Artist))
		{
			var wantedArtist = Normalize(track.Artist);
			var gotArtist = Normalize(foundArtist ?? "");
			if (wantedArtist.Length > 0 && gotArtist.Length > 0)
			{
				if (gotArtist.Contains(wantedArtist, StringComparison.Ordinal)
					|| wantedArtist.Contains(gotArtist, StringComparison.Ordinal))
					return true;
				// Artist names can differ purely by transliteration - a Japanese name against
				// its latin spelling, for instance. Accept that only in the strict pass, where
				// the artist was part of the query and the provider did the mapping itself, and
				// only when the two names really are written in different scripts. Without both
				// guards a title-only search hands the cover of any stranger with a song of the
				// same name - U2's "Zooropa" for the Japanese "Lemon", Kollegah for "Magnum Opus".
				return requireArtist && gotTitle == wantedTitle && DifferentScripts(wantedArtist, gotArtist);
			}
			if (requireArtist)
				return false;
		}

		// No artist to check against: only accept an exact title, otherwise the risk of
		// attaching a stranger's artwork is too high.
		return gotTitle == wantedTitle;
	}

	/// <summary>
	/// True when the two names are written in different writing systems, which is the only
	/// case where a mismatching artist name may still describe the same person. A latin
	/// spelling against another latin spelling is simply a different artist.
	/// </summary>
	private static bool DifferentScripts(string left, string right)
		=> IsLatin(left) != IsLatin(right);

	private static bool IsLatin(string value)
	{
		foreach (var character in value)
		{
			// Digits carry no script, so they must not decide the answer either way.
			if (char.IsLetter(character) && character > 0x024F)
				return false;
		}
		return true;
	}

	/// <summary>Exact match, or containment with enough shared length that it cannot be
	/// coincidence.</summary>
	private static bool TitleMatches(string got, string wanted)
	{
		if (got == wanted)
			return true;
		// Require a real overlap before trusting a partial match.
		if (wanted.Length < 10 || got.Length < 10)
			return false;
		if (!got.Contains(wanted, StringComparison.Ordinal) && !wanted.Contains(got, StringComparison.Ordinal))
			return false;
		// The shorter string must make up most of the longer one, so "Lemon" cannot match
		// "Lemon Tree Forever Remix".
		var shorter = Math.Min(got.Length, wanted.Length);
		var longer = Math.Max(got.Length, wanted.Length);
		return shorter / (double)longer >= 0.75;
	}


	/// <summary>
	/// Folds a title or artist down to comparable characters: strips punctuation and spacing,
	/// removes diacritics so "Söhne" equals "Sohne", and lowercases. Keeps every script, not
	/// just ASCII - dropping non-latin characters turned CJK names into empty strings and made
	/// every Japanese or Cyrillic track unmatchable.
	/// </summary>
	private static string Normalize(string value)
	{
		if (string.IsNullOrEmpty(value))
			return "";

		// FormD splits "ö" into "o" + combining diaeresis, so the mark can be dropped on its own.
		var decomposed = value.Normalize(NormalizationForm.FormD);
		var builder = new StringBuilder(decomposed.Length);
		foreach (var character in decomposed)
		{
			if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
				continue;
			if (char.IsLetterOrDigit(character))
				builder.Append(char.ToLowerInvariant(character));
		}
		return builder.ToString();
	}

	private static async Task<Image<Rgba32>?> DownloadImageAsync(string url, CancellationToken cancellationToken)
	{
		try
		{
			var bytes = await Http.GetByteArrayAsync(url, cancellationToken);
			if (bytes.Length is 0 or > 2_000_000)
				return null;
			return Image.Load<Rgba32>(bytes);
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>Draws the card over an already normalised cover (see <see cref="Normalise"/>).
	/// Mutates the image it is given, so callers must pass a copy they own.</summary>
	private static MemoryStream Card(Image<Rgba32> cover, Track track, double? progress)
	{
		var (titleFont, titleLines) = FitTitle(string.IsNullOrWhiteSpace(track.Title) ? "hostleaf.net" : track.Title);
		var artistFont = TextFamily.CreateFont(18);
		var artist = FitLine(track.Artist, artistFont, TextWidth);
		var creditFont = MarkFamily.CreateFont(13);
		// Leave room for the progress strip so the credit line never sits on top of it.
		var bottomInset = progress is null ? 0 : ProgressHeight + 3;
		var block = 18f + titleLines.Count * (titleFont.Size + 4) + (artist.Length > 0 ? 26 : 0) + 22 + bottomInset;
		ShadeBottom(cover, Size - block - 28);
		ShadeTop(cover, 62);

		cover.Mutate(ctx =>
		{
			DrawWordmark(ctx, 18, 16, 38);
			var y = Size - 18f - 16f - bottomInset;
			var credit = "powered by hostleaf.net";
			var creditWidth = TextMeasurer.MeasureSize(credit, new TextOptions(creditFont)).Width;
			ctx.DrawText(credit, creditFont, Color.FromRgba(255, 255, 255, 210), new PointF(Size - 20 - creditWidth, y));
			y -= 8;
			if (artist.Length > 0)
			{
				y -= 22;
				ctx.DrawText(Text(artistFont, new PointF(20, y)), artist, Color.FromRgba(255, 255, 255, 220));
				y -= 6;
			}
			for (var i = titleLines.Count - 1; i >= 0; i--)
			{
				y -= titleFont.Size + 4;
				ctx.DrawText(Text(titleFont, new PointF(20, y)), titleLines[i], Color.White);
			}
			if (progress is not null)
				DrawProgress(ctx, progress.Value);
		});
		return Save(cover);
	}

	private static Image<Rgba32> FillFrame(Image<Rgba32> source)
	{
		var scale = Size / (float)source.Width;
		var height = Math.Max(1, (int)Math.Round(source.Height * scale));
		if (height >= Size)
		{
			return source.Clone(ctx => ctx
				.Resize(new ResizeOptions
				{
					Size = new Size(Size, height),
					Mode = ResizeMode.Stretch,
					Sampler = KnownResamplers.Lanczos3,
				})
				.Crop(new Rectangle(0, (height - Size) / 2, Size, Size)));
		}

		var glow = source.Clone(ctx => ctx
			.Resize(new ResizeOptions
			{
				Size = new Size(Size, Size),
				Mode = ResizeMode.Crop,
				Sampler = KnownResamplers.Lanczos3,
				Position = AnchorPositionMode.Center,
			})
			.GaussianBlur(48)
			.Saturate(1.35f)
			.Brightness(1.08f));
		using var sharp = source.Clone(ctx => ctx.Resize(new ResizeOptions
		{
			Size = new Size(Size, height),
			Mode = ResizeMode.Stretch,
			Sampler = KnownResamplers.Lanczos3,
		}));
		glow.Mutate(ctx => ctx.DrawImage(sharp, new Point(0, (Size - height) / 2), 1f));
		return glow;
	}

	private static void TrimLetterbox(Image<Rgba32> image)
	{
		bool DarkRow(int y)
		{
			var dark = 0;
			var samples = 0;
			for (var x = 0; x < image.Width; x += 4)
			{
				samples++;
				if (Luma(image[x, y]) < 16)
					dark++;
			}
			return samples > 0 && dark >= samples * 0.92;
		}

		bool DarkCol(int x)
		{
			var dark = 0;
			var samples = 0;
			for (var y = 0; y < image.Height; y += 4)
			{
				samples++;
				if (Luma(image[x, y]) < 16)
					dark++;
			}
			return samples > 0 && dark >= samples * 0.92;
		}

		var top = 0;
		var bottom = image.Height - 1;
		var left = 0;
		var right = image.Width - 1;
		while (top < image.Height * 0.28 && DarkRow(top))
			top++;
		while (bottom > image.Height * 0.72 && DarkRow(bottom))
			bottom--;
		while (left < image.Width * 0.16 && DarkCol(left))
			left++;
		while (right > image.Width * 0.84 && DarkCol(right))
			right--;

		var width = right - left + 1;
		var height = bottom - top + 1;
		if (width < image.Width * 0.55 || height < image.Height * 0.55)
			return;
		if (top == 0 && left == 0 && bottom == image.Height - 1 && right == image.Width - 1)
			return;
		image.Mutate(ctx => ctx.Crop(new Rectangle(left, top, width, height)));
	}

	private static int Luma(Rgba32 pixel)
		=> (pixel.R * 54 + pixel.G * 183 + pixel.B * 19) >> 8;

	private static void TrimFlatBars(Image<Rgba32> image)
	{
		var top = ScanFlat(image, image.Height, true, true);
		var bottom = ScanFlat(image, image.Height, true, false);
		if (top + bottom > 0 && top + bottom < image.Height * 0.46)
			image.Mutate(ctx => ctx.Crop(new Rectangle(0, top, image.Width, image.Height - top - bottom)));

		var left = ScanFlat(image, image.Width, false, true);
		var right = ScanFlat(image, image.Width, false, false);
		if (left + right > 0 && left + right < image.Width * 0.62 && image.Width - left - right > 8)
			image.Mutate(ctx => ctx.Crop(new Rectangle(left, 0, image.Width - left - right, image.Height)));
	}

	private static int ScanFlat(Image<Rgba32> image, int limit, bool rows, bool fromStart)
	{
		var max = (int)(limit * 0.42);
		var found = 0;
		for (var i = 0; i < max; i++)
		{
			var index = fromStart ? i : limit - 1 - i;
			if (!IsFlatBar(image, index, rows))
				break;
			found++;
		}
		return found;
	}

	private static bool IsFlatBar(Image<Rgba32> image, int index, bool row)
	{
		long sum = 0;
		long sumSq = 0;
		var count = 0;
		var extent = row ? image.Width : image.Height;
		for (var i = 0; i < extent; i += 3)
		{
			var luma = row ? Luma(image[i, index]) : Luma(image[index, i]);
			sum += luma;
			sumSq += (long)luma * luma;
			count++;
		}
		if (count == 0)
			return false;
		var mean = sum / (double)count;
		var variance = sumSq / (double)count - mean * mean;
		return variance < 48 && mean < 78;
	}

	private static void ShadeTop(Image<Rgba32> image, int depth)
	{
		var height = Math.Clamp(depth, 1, Size);
		image.ProcessPixelRows(accessor =>
		{
			for (var y = 0; y < height; y++)
			{
				var keep = 1f - (0.62f * (1f - (y / (float)height)));
				var span = accessor.GetRowSpan(y);
				for (var x = 0; x < span.Length; x++)
				{
					span[x].R = (byte)(span[x].R * keep);
					span[x].G = (byte)(span[x].G * keep);
					span[x].B = (byte)(span[x].B * keep);
				}
			}
		});
	}

	// The wordmark is identical on every card, so it is prepared once instead of being loaded
	// from disk, rescaled and blurred on each render.
	private static Image<Rgba32>? wordmark;
	private static Image<Rgba32>? wordmarkHalo;
	private static int wordmarkHeight;
	private static readonly object WordmarkGate = new();

	private static void DrawWordmark(IImageProcessingContext ctx, int x, int y, int height)
	{
		PrepareWordmark(height);
		var mark = wordmark;
		var halo = wordmarkHalo;
		if (mark is null)
			return;
		if (halo is not null)
			ctx.DrawImage(halo, new Point(x, y + 1), 0.9f);
		ctx.DrawImage(mark, new Point(x, y), 1f);
	}

	private static void PrepareWordmark(int height)
	{
		if (wordmark is not null && wordmarkHeight == height)
			return;
		lock (WordmarkGate)
		{
			if (wordmark is not null && wordmarkHeight == height)
				return;
			if (!File.Exists(LogoPath))
				return;
			try
			{
				using var source = Image.Load<Rgba32>(LogoPath);
				var width = Math.Max(1, (int)Math.Round(height * (source.Width / (float)source.Height)));
				var scaled = source.Clone(img => img.Resize(new ResizeOptions
				{
					Size = new Size(width, height),
					Mode = ResizeMode.Stretch,
					Sampler = KnownResamplers.Lanczos3,
				}));

				// Drop shadow: the logo silhouette in black, blurred, drawn one pixel lower so
				// the mark stays readable on a bright cover.
				var halo = scaled.Clone();
				halo.ProcessPixelRows(accessor =>
				{
					for (var row = 0; row < accessor.Height; row++)
					{
						var span = accessor.GetRowSpan(row);
						for (var col = 0; col < span.Length; col++)
							span[col] = new Rgba32(0, 0, 0, span[col].A);
					}
				});
				halo.Mutate(img => img.GaussianBlur(2.4f));

				wordmark?.Dispose();
				wordmarkHalo?.Dispose();
				wordmark = scaled;
				wordmarkHalo = halo;
				wordmarkHeight = height;
			}
			catch (Exception)
			{
				// A missing or broken logo must not stop the card from rendering.
			}
		}
	}

	/// <summary>Progress strip along the bottom edge: shows at a glance how far the track has
	/// run, without needing an animated avatar - TeamSpeak does not support those.
	/// Drawn on an opaque dark track so it stays readable over a bright cover.</summary>
	private static void DrawProgress(IImageProcessingContext ctx, double ratio)
	{
		ratio = Math.Clamp(ratio, 0d, 1d);
		var y = Size - ProgressHeight;

		// Opaque track, not a translucent overlay: over a light cover a 150-alpha black is
		// barely darker than the image and the bar disappears.
		ctx.Fill(Color.ParseHex("0b0d0b"), new RectangleF(0, y, Size, ProgressHeight));
		// Hairline above the strip separates it from the artwork.
		ctx.Fill(Color.FromRgba(0, 0, 0, 90), new RectangleF(0, y - 1, Size, 1));

		var width = (float)(Size * ratio);
		if (width < 1f)
			return;
		ctx.Fill(Color.ParseHex("2be06a"), new RectangleF(0, y, width, ProgressHeight));
		// Bright leading edge marks the exact position even at a glance.
		if (width >= 3f)
			ctx.Fill(Color.ParseHex("eafff1"), new RectangleF(width - 2f, y, 2f, ProgressHeight));
	}

	/// <summary>The idle avatar never changes, so it is rendered once and replayed from bytes.</summary>
	private static MemoryStream Idle()
	{
		var cached = Volatile.Read(ref idleBytes);
		if (cached is null)
		{
			using var rendered = RenderIdle();
			cached = rendered.ToArray();
			Volatile.Write(ref idleBytes, cached);
		}
		return new MemoryStream(cached, writable: false);
	}

	private static byte[]? idleBytes;

	private static MemoryStream RenderIdle()
	{
		using var image = new Image<Rgba32>(Size, Size, Color.ParseHex("101210"));
		const string name = "hostleaf.net";
		var mark = MarkFamily.CreateFont(22);
		var markWidth = TextMeasurer.MeasureSize(name, new TextOptions(mark)).Width;
		const int logoHeight = 86;
		image.Mutate(ctx =>
		{
			if (File.Exists(LogoPath))
			{
				using var logo = Image.Load<Rgba32>(LogoPath);
				var logoWidth = Math.Max(1, (int)Math.Round(logoHeight * (logo.Width / (float)logo.Height)));
				logo.Mutate(img => img.Resize(new ResizeOptions
				{
					Size = new Size(logoWidth, logoHeight),
					Mode = ResizeMode.Stretch,
					Sampler = KnownResamplers.Lanczos3,
				}));
				ctx.DrawImage(logo, new Point((Size - logoWidth) / 2, 128), 1f);
			}
			ctx.DrawText(name, mark, Color.ParseHex("1db954"), new PointF((Size - markWidth) / 2f, 236));
		});
		return Save(image);
	}

	private static (Font Font, List<string> Lines) FitTitle(string text)
	{
		foreach (var size in new[] { 32f, 28f, 24f, 20f })
		{
			var font = TitleFamily.CreateFont(size);
			var lines = Wrap(text, font, TextWidth, 2);
			var clipped = lines.Exists(line => line.EndsWith('\u2026'));
			if (!clipped || size <= 20f)
				return (font, lines);
		}
		var fallback = TitleFamily.CreateFont(20);
		return (fallback, Wrap(text, fallback, TextWidth, 2));
	}

	private static List<string> Wrap(string text, Font font, float maxWidth, int maxLines)
	{
		var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		var lines = new List<string>();
		var index = 0;
		while (index < words.Length && lines.Count < maxLines)
		{
			var line = words[index++];
			while (index < words.Length && Measure(line + " " + words[index], font) <= maxWidth)
				line = line + " " + words[index++];
			var lastLine = lines.Count == maxLines - 1;
			if (lastLine && index < words.Length)
				line = FitLine(line + " " + string.Join(' ', words[index..]), font, maxWidth);
			else if (Measure(line, font) > maxWidth)
				line = FitLine(line, font, maxWidth);
			lines.Add(line);
			if (lastLine)
				break;
		}
		if (lines.Count == 0)
			lines.Add("");
		return lines;
	}

	private static string FitLine(string text, Font font, float maxWidth)
	{
		if (string.IsNullOrEmpty(text) || Measure(text, font) <= maxWidth)
			return text;
		var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		var line = "";
		foreach (var word in words)
		{
			var trial = line.Length == 0 ? word : line + " " + word;
			if (Measure(trial + "\u2026", font) > maxWidth)
				break;
			line = trial;
		}
		if (line.Length == 0)
		{
			line = text;
			while (line.Length > 1 && Measure(line + "\u2026", font) > maxWidth)
				line = line[..^1];
		}
		return line + "\u2026";
	}

	private static float Measure(string text, Font font)
		=> TextMeasurer.MeasureSize(text, Text(font, PointF.Empty)).Width;

	private static void ShadeBottom(Image<Rgba32> image, float from)
	{
		var start = Math.Clamp((int)from, 0, Size - 1);
		image.ProcessPixelRows(accessor =>
		{
			for (var y = start; y < Size; y++)
			{
				var fade = (y - start) / (float)(Size - start);
				var keep = 1f - (fade * 0.5f);
				var row = accessor.GetRowSpan(y);
				for (var x = 0; x < row.Length; x++)
				{
					row[x].R = (byte)(row[x].R * keep);
					row[x].G = (byte)(row[x].G * keep);
					row[x].B = (byte)(row[x].B * keep);
				}
			}
		});
	}


	/// <summary>Encodes to JPEG, stepping the quality down until the avatar fits the server
	/// limit. Each attempt reuses the same buffer instead of leaving the rejected ones behind.</summary>
	private static MemoryStream Save(Image image)
	{
		var mem = new MemoryStream(AvatarSizeLimit);
		foreach (var quality in JpegQualitySteps)
		{
			mem.SetLength(0);
			image.Save(mem, new JpegEncoder { Quality = quality });
			if (mem.Length <= AvatarSizeLimit)
				break;
		}
		mem.Position = 0;
		return mem;
	}

	private static FontFamily LoadFamily(string file)
	{
		var collection = new FontCollection();
		return collection.Add(InterPath + file);
	}

	/// <summary>
	/// Families used when the main face has no glyph for a character. Without this, Japanese,
	/// Chinese, Korean and Cyrillic titles render as empty boxes, because Inter only covers
	/// latin and greek. Missing files are skipped, so the card still renders on a host that
	/// has no CJK fonts installed.
	/// </summary>
	private static readonly FontFamily[] FallbackFamilies = LoadFallbacks();

	private static FontFamily[] LoadFallbacks()
	{
		string[] candidates =
		[
			"/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
			"/usr/share/fonts/opentype/noto/NotoSansCJK-Medium.ttc",
			"/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc",
		];

		var collection = new FontCollection();
		var families = new List<FontFamily>();
		foreach (var path in candidates)
		{
			try
			{
				if (!File.Exists(path))
					continue;
				// A .ttc holds several families; add them all and take the first.
				var added = collection.AddCollection(path);
				foreach (var family in added)
				{
					families.Add(family);
					break;
				}
			}
			catch (Exception)
			{
				// A broken or unreadable font must never stop a card from rendering.
			}
		}
		return [.. families];
	}

	/// <summary>Text options with the fallback chain applied, so mixed-script titles render
	/// completely instead of showing tofu boxes.</summary>
	private static RichTextOptions Text(Font font, PointF origin)
		=> new(font) { Origin = origin, FallbackFontFamilies = FallbackFamilies };

	private static HttpClient CreateHttp()
	{
		var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
		http.DefaultRequestHeaders.UserAgent.ParseAdd("hostleaf-nowplaying/1.0 (https://hostleaf.net)");
		return http;
	}
}
