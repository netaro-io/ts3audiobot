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
using System.Threading;
using TS3AudioBot.Config;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists.Shuffle;
using TS3AudioBot.Web.Model;
using TSLib.Helper;

namespace TS3AudioBot.Playlists;

public sealed class PlaylistManager
{
	private readonly PlaylistIO playlistPool;
	private const string mixName = ".mix";
	private readonly Playlist mixList = new() { Title = "Now Playing" };
	private readonly Lock listLock = new();
	private readonly List<int> shuffleBag = [];
	private int shuffleBagCount = -1;
	public IReadOnlyPlaylist CurrentList => mixList;

	private IShuffleAlgorithm shuffle;

	private readonly IShuffleAlgorithm NormalOrder = new NormalOrder();
	private readonly IShuffleAlgorithm RandomOrder = new LinearFeedbackShiftRegister();

	public int Index { get => shuffle.Index; set => shuffle.Index = value; }

	public PlaylistItem? Current => MoveIndex(null, true);

	private bool random;
	public bool Random
	{
		get => random;
		set
		{
			random = value;
			var index = shuffle.Index;
			if (random)
				shuffle = RandomOrder;
			else
				shuffle = NormalOrder;
			shuffle.Index = index;
			InvalidateShuffleBag();
		}
	}

	public int Seed { get => shuffle.Seed; set => shuffle.Seed = value; }

	/// <summary>Loop mode for the current playlist.</summary>
	public LoopMode Loop { get; set; } = LoopMode.Off;

	public PlaylistManager(ConfPlaylists _, PlaylistIO playlistPool)
	{
		this.playlistPool = playlistPool;
		shuffle = NormalOrder;
	}

	public PlaylistItem? Next(bool manually = true) => MoveIndex(forward: true, manually);

	public PlaylistItem? PeekNext(bool manually = false) => MoveIndex(true, manually, false);


	public PlaylistItem? Previous(bool manually = true) => MoveIndex(forward: false, manually);

	internal PlaylistItem? MoveIndex(bool? forward, bool manually, bool consume = true)
	{
		lock (listLock)
		{
			if (mixList.Items.Count == 0)
				return null;

			if (shuffle.Length != mixList.Items.Count)
				shuffle.Length = mixList.Items.Count;
			if (shuffle.Index < 0 || shuffle.Index >= mixList.Items.Count)
				shuffle.Index = 0;

			// When next/prev was requested manually (via command) we ignore the loop one
			// mode and instead move the index.
			if ((Loop == LoopMode.One && !manually) || forward is null)
				return mixList[shuffle.Index];

			if (!consume && forward == true && !random)
			{
				if (Loop == LoopMode.Off && !manually && shuffle.Index == mixList.Items.Count - 1)
					return null;
				var peeked = (shuffle.Index + 1) % mixList.Items.Count;
				return mixList[peeked];
			}

			if (random && forward == true)
			{
				// Shuffle bag: every track plays exactly once per pass, then the bag is
				// reshuffled. That is what listeners expect from "random" - a plain RNG repeats
				// and starves tracks.
				if (shuffleBagCount != mixList.Items.Count)
					RefillShuffleBag();

				if (shuffleBag.Count == 0)
				{
					// Pass complete. Without looping this is the end of the queue, and a peek
					// must not refill the bag - that is a side effect the caller does not expect.
					if (Loop == LoopMode.Off && !manually)
						return null;
					if (!consume)
						return PeekFreshBagItem();
					RefillShuffleBag();
					if (shuffleBag.Count == 0)
						return mixList[shuffle.Index];
				}

				var nextIndex = shuffleBag[^1];
				if (!consume)
					return mixList[nextIndex];
				shuffleBag.RemoveAt(shuffleBag.Count - 1);
				shuffle.Index = nextIndex;
				return mixList[nextIndex];
			}

			bool listEnded;
			if (forward == true)
				listEnded = shuffle.Next();
			else
				listEnded = shuffle.Prev();

			// Get a new seed when one play-through ended.
			if (listEnded && Random)
				SetRandomSeed();

			// If a next/prev request goes over the bounds of the list while loop mode is off
			// but was requested manually we act as if the list was looped.
			// This will give a more intuitive behaviour when the list is shuffled (and also if not)
			// as the end might not be clear or visible.
			if (Loop == LoopMode.Off && listEnded && !manually)
				return null;

			return mixList[shuffle.Index];
		}
	}

	public void Queue(PlaylistItem item)
		=> ModifyPlaylist(mixName, mix => { mix.Add(item).UnwrapThrow(); InvalidateShuffleBag(); });

	public void Queue(IEnumerable<PlaylistItem> items)
		=> ModifyPlaylist(mixName, mix => { mix.AddRange(items).UnwrapThrow(); InvalidateShuffleBag(); });

	public void Clear()
		=> ModifyPlaylist(mixName, mix => { mix.Clear(); InvalidateShuffleBag(); });

	/// <summary>Forces a reshuffle on the next draw. Bag entries are positions, so any change to
	/// the queue makes them point at different tracks.</summary>
	private void InvalidateShuffleBag()
	{
		shuffleBag.Clear();
		shuffleBagCount = -1;
	}

	private void SetRandomSeed()
	{
		shuffle.Seed = System.Random.Shared.Next();
	}

	/// <summary>Refills the bag with every index except the one playing right now, in a
	/// uniformly shuffled order (Fisher-Yates). Items are taken from the end, so removing one
	/// never shifts the rest.</summary>
	private void RefillShuffleBag()
	{
		shuffleBag.Clear();
		shuffleBagCount = mixList.Items.Count;
		for (var i = 0; i < mixList.Items.Count; i++)
		{
			if (i != shuffle.Index)
				shuffleBag.Add(i);
		}
		for (var i = shuffleBag.Count - 1; i > 0; i--)
		{
			var swap = System.Random.Shared.Next(i + 1);
			(shuffleBag[i], shuffleBag[swap]) = (shuffleBag[swap], shuffleBag[i]);
		}
	}

	/// <summary>Which track a fresh bag would yield, without actually creating one.
	/// Lets the prefetch look past the end of a pass while looping, side-effect free.</summary>
	private PlaylistItem? PeekFreshBagItem()
	{
		var count = mixList.Items.Count;
		if (count == 0)
			return null;
		if (count == 1)
			return mixList[shuffle.Index];
		var candidate = System.Random.Shared.Next(count - 1);
		if (candidate >= shuffle.Index)
			candidate++;
		return mixList[candidate];
	}

	public R<IReadOnlyPlaylist, LocalStr> LoadPlaylist(string listId)
	{
		R<Playlist, LocalStr> res;
		if (listId.StartsWith('.'))
		{
			res = GetSpecialPlaylist(listId);
		}
		else
		{
			if (!Util.IsSafeFileName(listId).GetOk(out var error))
				return error;
			res = playlistPool.Read(listId);
		}

		if (!res.Ok)
			return res.Error;
		return res.Value;
	}

	public E<LocalStr> CreatePlaylist(string listId, string? title = null)
	{
		if (!Util.IsSafeFileName(listId).GetOk(out var error))
			return error;
		if (playlistPool.Exists(listId))
			return new LocalStr("Already exists");
		return playlistPool.Write(listId, new Playlist().SetTitle(title ?? listId));
	}

	public bool ExistsPlaylist(string listId)
	{
		if (GetSpecialPlaylist(listId))
			return true;
		if (!Util.IsSafeFileName(listId))
			return false;
		return playlistPool.Exists(listId);
	}

	public E<LocalStr> ModifyPlaylist(string listId, Action<Playlist> action)
	{
		var res = GetSpecialPlaylist(listId);
		if (res.GetOk(out var plist))
		{
			lock (listLock)
			{
				action(plist);
			}
			return R.Ok;
		}
		else
		{
			if (!Util.IsSafeFileName(listId).GetOk(out var error))
				return error;
			if (!playlistPool.Read(listId).Get(out plist, out error))
				return error;
			lock (listLock)
			{
				action(plist);
			}
			return playlistPool.Write(listId, plist);
		}
	}

	public E<LocalStr> DeletePlaylist(string listId)
	{
		if (!Util.IsSafeFileName(listId).GetOk(out var error))
			return error;

		return playlistPool.Delete(listId);
	}

	public R<PlaylistInfo[], LocalStr> GetAvailablePlaylists(string? pattern = null) => playlistPool.ListPlaylists(pattern);

	private R<Playlist, LocalStr> GetSpecialPlaylist(string listId)
	{
		return listId switch
		{
			mixName => mixList,
			_ => new LocalStr(strings.error_playlist_special_not_found),
		};
	}
}
