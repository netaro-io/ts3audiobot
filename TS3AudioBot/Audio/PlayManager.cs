// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Config;
using TS3AudioBot.Environment;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TSLib.Helper;
using TSLib.Scheduler;

namespace TS3AudioBot.Audio;

/// <summary>Provides a interface for enqueuing, playing and registering song events.</summary>
public class PlayManager
{
	private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

	/// <summary>Tick interval of the crossfade state machine.</summary>
	private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
	/// <summary>Overlap used for a manual skip - short, because the user expects it to react.</summary>
	private static readonly TimeSpan ManualSkipFade = TimeSpan.FromMilliseconds(1200);
	/// <summary>Upper bound for the configurable crossfade length, in seconds.</summary>
	private const int MaxCrossfadeSeconds = 20;
	/// <summary>Tracks shorter than this never crossfade - it would eat most of the song.</summary>
	private static readonly TimeSpan MinTrackForFade = TimeSpan.FromSeconds(8);
	/// <summary>How long a manual skip waits for a prefetch that is still resolving.</summary>
	private const int PrefetchWaitMs = 1500;

	private readonly ConfBot confBot;
	private readonly Player playerConnection;
	private readonly PlaylistManager playlistManager;
	private readonly ResolveContext resourceResolver;
	private readonly Stats stats;
	private readonly DedicatedTaskScheduler scheduler;
	private TickWorker? crossfadeTicker;
	private bool tickerRequested;
	private bool crossfadeBusy;
	private int crossfadeGeneration;
	private CrossfadePhase crossfadePhase;
	private PlaylistItem? armedItem;
	private PlayResource? armedResource;
	private int armedFromIndex;

	public PlayInfoEventArgs? CurrentPlayData { get; private set; }
	public bool IsPlaying => CurrentPlayData != null;
	private CancellationTokenSource? playRequest = null;

	public event AsyncEventHandler<PlayInfoEventArgs>? OnResourceUpdated;
	public event AsyncEventHandler<PlayInfoEventArgs>? BeforeResourceStarted;
	public event AsyncEventHandler<PlayInfoEventArgs>? AfterResourceStarted;
	public event AsyncEventHandler<SongEndEventArgs>? ResourceStopped;
	public event AsyncEventHandler? PlaybackStopped;

	public PlayManager(ConfBot config, Player playerConnection, PlaylistManager playlistManager, ResolveContext resourceResolver, Stats stats, DedicatedTaskScheduler scheduler)
	{
		confBot = config;
		this.playerConnection = playerConnection;
		this.playlistManager = playlistManager;
		this.resourceResolver = resourceResolver;
		this.stats = stats;
		this.scheduler = scheduler;
	}

	public Task Enqueue(InvokerData invoker, AudioResource ar, PlayInfo? meta = null) => Enqueue(invoker, new PlaylistItem(ar, meta));
	public async Task Enqueue(InvokerData invoker, string message, string? audioType = null, PlayInfo? meta = null)
	{
		PlayResource playResource;
		try { playResource = await resourceResolver.Load(message, CancellationToken.None, audioType); }
		catch
		{
			stats.TrackSongLoad(audioType, false, true);
			throw;
		}
		await Enqueue(invoker, PlaylistItem.From(playResource).MergeMeta(meta));
	}
	public Task Enqueue(InvokerData invoker, IEnumerable<PlaylistItem> items)
	{
		var startOff = playlistManager.CurrentList.Items.Count;
		playlistManager.Queue(items.Select(x => UpdateItem(x, invoker)));
		return PostEnqueue(invoker, startOff);
	}
	public Task Enqueue(InvokerData invoker, PlaylistItem item)
	{
		var startOff = playlistManager.CurrentList.Items.Count;
		playlistManager.Queue(UpdateItem(item, invoker));
		return PostEnqueue(invoker, startOff);
	}

	private static PlaylistItem UpdateItem(PlaylistItem item, InvokerData invoker)
	{
		item.PlayInfo ??= new PlayInfo();
		item.PlayInfo.ResourceOwnerUid = invoker.ClientUid;
		return item;
	}

	private async Task PostEnqueue(InvokerData invoker, int startIndex)
	{
		if (IsPlaying)
			return;
		playlistManager.Index = startIndex;
		await StartCurrent(invoker);
	}

	/// <summary>Tries to play the passed <see cref="AudioResource"/></summary>
	/// <param name="invoker">The invoker of this resource. Used for responses and association.</param>
	/// <param name="ar">The resource to load and play.</param>
	/// <param name="meta">Allows overriding certain settings for the resource. Can be null.</param>
	/// <returns>Ok if successful, or an error message otherwise.</returns>
	public async Task Play(InvokerData invoker, AudioResource ar, PlayInfo? meta = null)
	{
		ArgumentNullException.ThrowIfNull(ar);

		PlayResource playResource;
		try
		{
			playResource = await WithPlayRequestCancelToken(async ct => await resourceResolver.Load(ar, ct));
		}
		catch
		{
			stats.TrackSongLoad(ar.AudioType, false, true);
			throw;
		}
		await Play(invoker, playResource.MergeMeta(meta));
	}

	/// <summary>Tries to play the passed link.</summary>
	/// <param name="invoker">The invoker of this resource. Used for responses and association.</param>
	/// <param name="link">The link to resolve, load and play.</param>
	/// <param name="audioType">The associated resource type string to a factory.</param>
	/// <param name="meta">Allows overriding certain settings for the resource. Can be null.</param>
	/// <returns>Ok if successful, or an error message otherwise.</returns>
	public async Task Play(InvokerData invoker, string link, string? audioType = null, PlayInfo? meta = null)
	{
		PlayResource playResource;
		try
		{
			playResource = await WithPlayRequestCancelToken(async ct => await resourceResolver.Load(link, ct, audioType));
		}
		catch
		{
			stats.TrackSongLoad(audioType, false, true);
			throw;
		}
		await Play(invoker, playResource.MergeMeta(meta));
	}

	public async Task Play(InvokerData invoker, IEnumerable<PlaylistItem> items, int index = 0)
	{
		playlistManager.Clear();
		playlistManager.Queue(items.Select(x => UpdateItem(x, invoker)));
		playlistManager.Index = index;
		await StartCurrent(invoker);
	}

	public async Task Play(InvokerData invoker, PlaylistItem item)
	{
		ArgumentNullException.ThrowIfNull(item);

		if (item.AudioResource is null)
			throw new Exception("Invalid playlist item");
		playlistManager.Clear();
		playlistManager.Queue(item);
		playlistManager.Index = 0;
		await StartResource(invoker, item);
	}

	public Task Play(InvokerData invoker) => StartCurrent(invoker);

	/// <summary>Plays the passed <see cref="PlayResource"/></summary>
	/// <param name="invoker">The invoker of this resource. Used for responses and association.</param>
	/// <param name="play">The associated resource type string to a factory.</param>
	/// <param name="meta">Allows overriding certain settings for the resource.</param>
	/// <returns>Ok if successful, or an error message otherwise.</returns>
	public async Task Play(InvokerData invoker, PlayResource play)
	{
		playlistManager.Clear();
		playlistManager.Queue(PlaylistItem.From(play));
		playlistManager.Index = 0;
		stats.TrackSongLoad(play.AudioResource.AudioType, true, true);
		await StartResource(invoker, play);
	}

	private async Task StartCurrent(InvokerData invoker, bool manually = true)
	{
		var pli = playlistManager.Current;
		if (pli is null)
			throw Error.LocalStr(strings.error_playlist_is_empty);
		try
		{
			await StartResource(invoker, pli);
		}
		catch (AudioBotException ex)
		{
			Log.Warn("Skipping: {0} because {1}", pli, ex.Message);
			await Next(invoker, manually);
		}
	}

	private async Task StartResource(InvokerData invoker, PlaylistItem item)
	{
		PlayResource playResource;
		try
		{
			playResource = await WithPlayRequestCancelToken(async ct => await resourceResolver.Load(item.AudioResource, ct));
		}
		catch
		{
			stats.TrackSongLoad(item.AudioResource.AudioType, false, false);
			throw;
		}
		stats.TrackSongLoad(item.AudioResource.AudioType, true, false);
		await StartResource(invoker, playResource.MergeMeta(item.PlayInfo));
	}

	private async Task StartResource(InvokerData invoker, PlayResource play)
	{
		var sourceLink = resourceResolver.RestoreLink(play.AudioResource);
		var playInfo = new PlayInfoEventArgs(invoker, play, sourceLink);
		await BeforeResourceStarted.InvokeAsync(this, playInfo);

		if (string.IsNullOrWhiteSpace(play.PlayUri))
		{
			Log.Error("Internal resource error: link is empty (resource:{0})", play);
			throw Error.LocalStr(strings.error_playmgr_internal_error);
		}

		Log.Debug("AudioResource start: {0}", play);
		try { await playerConnection.Play(play); }
		catch (AudioBotException ex)
		{
			Log.Error("Error return from player: {0}", ex.Message);
			throw Error.Exception(ex).LocalStr(strings.error_playmgr_internal_error);
		}

		playerConnection.Volume = Math.Clamp(playerConnection.Volume, confBot.Audio.Volume.Min, confBot.Audio.Volume.Max);
		CurrentPlayData = playInfo; // TODO meta as readonly
		await AfterResourceStarted.InvokeAsync(this, playInfo);
		EnsureCrossfadeTicker();
	}

	public async Task Next(InvokerData invoker, bool manually = true)
	{
		// A skip during a running fade: finish it instantly rather than layering a third track.
		if (playerConnection.CrossfadeActive)
		{
			playerConnection.CompleteCrossfade(true);
			ClearArmed();
		}
		if (await TryUsePrefetch(invoker, manually))
			return;
		CancelCrossfade();
		PlaylistItem? pli = null;
		for (int i = 0; i < 10; i++)
		{
			pli = playlistManager.Next(manually);
			if (pli is null) break;
			try
			{
				await StartResource(invoker, pli);
				return;
			}
			catch (AudioBotException ex) { Log.Warn("Skipping: {0} because {1}", pli, ex.Message); }
		}
		if (pli is null)
			throw Error.LocalStr(strings.info_playmgr_no_next_song);
		else
			throw Error.LocalStr(string.Format(strings.error_playmgr_many_songs_failed, "!next"));
	}

	public async Task Previous(InvokerData invoker, bool manually = true)
	{
		CancelCrossfade();
		PlaylistItem? pli = null;
		for (int i = 0; i < 10; i++)
		{
			pli = playlistManager.Previous(manually);
			if (pli is null) break;
			try
			{
				await StartResource(invoker, pli);
				return;
			}
			catch (AudioBotException ex) { Log.Warn("Skipping: {0} because {1}", pli, ex.Message); }
		}
		if (pli is null)
			throw Error.LocalStr(strings.info_playmgr_no_previous_song);
		else
			throw Error.LocalStr(string.Format(strings.error_playmgr_many_songs_failed, "!previous"));
	}

	public async Task SongStoppedEvent(object? sender, EventArgs e)
	{
		// The outgoing decoder reached its end while the fade was still running - the incoming
		// track is already live, so this is not a queue end.
		if (playerConnection.CrossfadeActive)
		{
			playerConnection.CompleteCrossfade(false);
			ClearArmed();
			return;
		}
		if (playerConnection.TakeSuppressedEnd())
			return;
		await StopInternal(true);
	}

	public Task Stop()
	{
		CancelCrossfade();
		return StopInternal(false);
	}

	private async Task StopInternal(bool songEndedByCallback)
	{
		await ResourceStopped.InvokeAsync(this, new SongEndEventArgs(songEndedByCallback));

		if (songEndedByCallback)
		{
			try
			{
				await Next(CurrentPlayData?.Invoker ?? InvokerData.Anonymous, false);
				return;
			}
			catch (AudioBotException ex) { Log.Info("Song queue ended: {0}", ex.Message); }
		}
		else
		{
			await playerConnection.Stop();
		}

		CurrentPlayData = null;
		PlaybackStopped?.Invoke(this, EventArgs.Empty);
	}

	public async Task Update(SongInfoChanged newInfo)
	{
		var data = CurrentPlayData;
		if (data is null)
			return;
		if (newInfo.Title != null)
			data.ResourceData.ResourceTitle = newInfo.Title;
		// further properties...
		try
		{
			await OnResourceUpdated.InvokeAsync(this, data);
		}
		catch (AudioBotException ex)
		{
			Log.Warn(ex, "Error in OnResourceUpdated event.");
		}
	}

	public static PlayInfo? ParseAttributes(string[] attrs)
	{
		if (attrs is null || attrs.Length == 0)
			return null;

		var meta = new PlayInfo();
		foreach (var attr in attrs)
		{
			if (attr.StartsWith('@'))
			{
				meta.StartOffset = TextUtil.ParseTime(attr[1..]);
			}
		}
		return meta;
	}

	private void EnsureCrossfadeTicker()
	{
		if (tickerRequested)
			return;
		tickerRequested = true;
		_ = scheduler.InvokeAsync(() =>
		{
			crossfadeTicker ??= scheduler.CreateTimer(() => { _ = OnCrossfadeTick(); }, TickInterval, true);
			return Task.CompletedTask;
		});
	}

	private async Task OnCrossfadeTick()
	{
		if (crossfadeBusy)
			return;
		crossfadeBusy = true;
		try
		{
			await StepCrossfade();
		}
		catch (OperationCanceledException)
		{
		}
		catch (AudioBotException ex)
		{
			Log.Debug("Crossfade skipped: {0}", ex.Message);
			ClearArmed();
		}
		catch (Exception ex)
		{
			// The ticker must survive anything: if it dies, playback stops advancing silently.
			Log.Warn(ex, "Unexpected error in the crossfade tick");
			ClearArmed();
		}
		finally
		{
			crossfadeBusy = false;
		}
	}

	private async Task StepCrossfade()
	{
		if (!IsPlaying)
			return;

		// Phase 1: a fade is running - keep feeding it and close it out once the ramp finished.
		if (playerConnection.CrossfadeActive)
		{
			playerConnection.PumpCrossfade();
			if (playerConnection.CrossfadeSettled)
			{
				playerConnection.CompleteCrossfade(true);
				ClearArmed();
			}
			return;
		}

		if (crossfadePhase is CrossfadePhase.Loading or CrossfadePhase.Starting)
			return;

		// Phase 2: next track is decoded and waiting - start the fade when the tail is reached.
		if (crossfadePhase == CrossfadePhase.Ready && armedResource is not null && armedItem is not null)
		{
			if (playlistManager.Index != armedFromIndex)
			{
				ClearArmed();
			}
			else
			{
				var fade = FadeLength();
				var length = playerConnection.Length;
				var position = playerConnection.Position;
				if (fade <= TimeSpan.Zero || length is null || position is null || length.Value - position.Value > fade)
					return;

				var moved = playlistManager.Next(false);
				if (moved?.AudioResource?.ResourceId != armedItem.AudioResource.ResourceId)
				{
					ClearArmed();
					return;
				}

				// Hold the phase across the await so a second tick cannot start a second fade.
				crossfadePhase = CrossfadePhase.Starting;
				var starting = armedResource;
				try
				{
					await playerConnection.BeginCrossfade(starting.PlayUri, starting.PlayInfo?.StartOffset, fade);
				}
				catch (Exception ex)
				{
					Log.Warn(ex, "Could not start crossfade, falling back to a direct cut");
					ClearArmed();
					return;
				}
				ClearArmed();
				await Announce(starting, CurrentPlayData?.Invoker ?? InvokerData.Anonymous);
				return;
			}
		}

		// Phase 3: nothing armed - resolve the upcoming track ahead of time.
		var fromIndex = playlistManager.Index;
		var peek = playlistManager.PeekNext(false);
		if (peek?.AudioResource is null)
			return;
		crossfadePhase = CrossfadePhase.Loading;
		var generation = ++crossfadeGeneration;
		PlayResource loaded;
		try
		{
			loaded = await WithPlayRequestCancelToken(ct => resourceResolver.Load(peek.AudioResource, ct));
		}
		catch
		{
			if (generation == crossfadeGeneration)
				crossfadePhase = CrossfadePhase.Idle;
			return;
		}
		if (generation != crossfadeGeneration || playlistManager.Index != fromIndex)
		{
			if (generation == crossfadeGeneration)
				crossfadePhase = CrossfadePhase.Idle;
			return;
		}
		armedItem = peek;
		armedResource = loaded;
		armedFromIndex = fromIndex;
		crossfadePhase = CrossfadePhase.Ready;
	}

	private void ClearArmed()
	{
		crossfadePhase = CrossfadePhase.Idle;
		armedResource = null;
		armedItem = null;
	}

	/// <summary>Overlap for an automatic track change, capped so that a short track cannot be
	/// swallowed by its own fade.</summary>
	private TimeSpan FadeLength()
	{
		var seconds = Math.Clamp(confBot.Audio.Crossfade.Value, 0, MaxCrossfadeSeconds);
		var length = playerConnection.Length;
		if (seconds <= 0 || length is null || length.Value < MinTrackForFade)
			return TimeSpan.Zero;
		var fade = TimeSpan.FromSeconds(seconds);
		// Never spend more than 45 percent of the track on the fade.
		var ceiling = TimeSpan.FromSeconds(length.Value.TotalSeconds * 0.45);
		return fade > ceiling ? ceiling : fade;
	}

	private async Task Announce(PlayResource play, InvokerData invoker)
	{
		var sourceLink = resourceResolver.RestoreLink(play.AudioResource);
		var playInfo = new PlayInfoEventArgs(invoker, play, sourceLink);
		await BeforeResourceStarted.InvokeAsync(this, playInfo);
		CurrentPlayData = playInfo;
		await AfterResourceStarted.InvokeAsync(this, playInfo);
	}

	/// <summary>Uses the already decoded next track for a manual skip, so <c>!next</c> overlaps
	/// instead of cutting. Returns false when nothing usable is armed and the caller should
	/// fall back to a normal start.</summary>
	private async Task<bool> TryUsePrefetch(InvokerData invoker, bool manually)
	{
		// Wait briefly if a prefetch is still resolving - a skip right after a track change
		// would otherwise always miss it. Bounded so a dead resolver cannot hang the command.
		if (crossfadePhase == CrossfadePhase.Loading)
		{
			var deadline = System.Environment.TickCount64 + PrefetchWaitMs;
			while (crossfadePhase == CrossfadePhase.Loading && System.Environment.TickCount64 < deadline)
				await Task.Delay(25);
		}
		if (crossfadePhase != CrossfadePhase.Ready || armedResource is null || armedItem?.AudioResource is null)
			return false;
		if (playlistManager.Index != armedFromIndex)
			return false;
		var peek = playlistManager.PeekNext(manually);
		if (peek?.AudioResource?.ResourceId != armedItem.AudioResource.ResourceId)
			return false;

		var play = armedResource;
		var item = armedItem;
		crossfadePhase = CrossfadePhase.Starting;
		playlistManager.Next(manually);
		try
		{
			await playerConnection.BeginCrossfade(play.PlayUri, play.PlayInfo?.StartOffset, ManualSkipFade);
		}
		catch (Exception ex)
		{
			// The index already advanced, so starting the track directly here keeps the queue
			// honest. Returning false would make the caller skip one song.
			Log.Warn(ex, "Prefetched skip failed, starting the track directly");
			ClearArmed();
			await StartResource(invoker, item);
			return true;
		}
		ClearArmed();
		await Announce(play, invoker);
		return true;
	}

	/// <summary>Invalidates any pending prefetch and drops a prepared incoming track.
	/// Bumping the generation makes an in-flight resolve discard its own result.</summary>
	private void CancelCrossfade()
	{
		crossfadeGeneration++;
		ClearArmed();
		playerConnection.DropIncoming();
	}

	private enum CrossfadePhase
	{
		Idle,
		/// <summary>Resolving the next track (network, metadata).</summary>
		Loading,
		/// <summary>Next track resolved, waiting for the tail of the current one.</summary>
		Ready,
		/// <summary>Spawning the decoder for the fade; blocks a second concurrent start.</summary>
		Starting,
	}

	private async Task<T> WithPlayRequestCancelToken<T>(Func<CancellationToken, Task<T>> request)
	{
		using var cts = new CancellationTokenSource();
		try
		{
			playRequest?.Cancel();
			playRequest = cts;
			T res = await request(cts.Token);
			return res;
		}
		finally
		{
			if (playRequest == cts) playRequest = null;
		}
	}
}
