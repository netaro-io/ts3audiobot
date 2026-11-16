// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Config;
using TS3AudioBot.Helper;
using TS3AudioBot.ResourceFactories;
using TSLib;
using TSLib.Audio;
using TSLib.Helper;
using TSLib.Scheduler;

namespace TS3AudioBot.Audio;

public sealed class Player : IDisposable
{
	private const Codec SendCodec = Codec.OpusMusic;

	/// <summary>Hard cut fade. Long enough to avoid a click, short enough to feel instant.</summary>
	private static readonly TimeSpan FadeOutHard = TimeSpan.FromMilliseconds(45);
	private static readonly TimeSpan FadeInHard = TimeSpan.FromMilliseconds(45);
	private static readonly TimeSpan FadeOutStop = TimeSpan.FromMilliseconds(90);
	/// <summary>Stille nach dem Fade. Deckt den Opus-Overlap und den Anlauf des
	/// Client-Puffers ab, ohne dass sich das Stoppen träge anfühlt.</summary>
	private static readonly TimeSpan SilenceTail = TimeSpan.FromMilliseconds(120);
	private static readonly TimeSpan MinFade = TimeSpan.FromMilliseconds(200);
	/// <summary>Audio the incoming track must have buffered before the fade starts: 100 ms.</summary>
	private const int PrerollBytes = 48000 / 10 * 2 * 2;
	private static readonly TimeSpan PrerollTimeout = TimeSpan.FromMilliseconds(400);
	private const int RampGraceMs = 60;

	private readonly DedicatedTaskScheduler scheduler;

	private readonly FfmpegProducer decoderA;
	private readonly FfmpegProducer decoderB;
	private FfmpegProducer live;
	private FfmpegProducer spare;
	private GainProducer? liveGain;
	private GainProducer? spareGain;
	private PullBuffer? liveBuffer;
	private PullBuffer? spareBuffer;
	private DateTime fadeArm;
	private TimeSpan fadeDuration;
	private bool incomingMixed;
	private int suppressedEnds;

	public IPlayerSource? CurrentPlayerSource { get; private set; }
	public StallCheckPipe StallCheckPipe { get; }
	public VolumePipe VolumePipe { get; }
	public FfmpegProducer FfmpegProducer => live;
	public PreciseTimedPipe TimePipe { get; }
	public PassiveMergePipe MergePipe { get; }
	public EncoderPipe EncoderPipe { get; }
	public IAudioPassiveConsumer? PlayerSink { get; private set; }
	public bool CrossfadeActive { get; private set; }
	public bool CrossfadeSettled => incomingMixed && (liveGain?.Settled ?? false);

	public Player(ConfRoot confRoot, ConfBot config, DedicatedTaskScheduler scheduler, Id id)
	{
		this.scheduler = scheduler;

		decoderA = new FfmpegProducer(confRoot.Tools.Ffmpeg, scheduler, id)
		{
			BassDb = config.Audio.Bass,
			TrebleDb = config.Audio.Treble,
		};
		decoderB = new FfmpegProducer(confRoot.Tools.Ffmpeg, scheduler, id)
		{
			BassDb = config.Audio.Bass,
			TrebleDb = config.Audio.Treble,
		};
		live = decoderA;
		spare = decoderB;
		config.Audio.Bass.Changed += (s, e) =>
		{
			decoderA.BassDb = e.NewValue;
			decoderB.BassDb = e.NewValue;
		};
		config.Audio.Treble.Changed += (s, e) =>
		{
			decoderA.TrebleDb = e.NewValue;
			decoderB.TrebleDb = e.NewValue;
		};
		StallCheckPipe = new StallCheckPipe();
		VolumePipe = new VolumePipe();
		Volume = config.Audio.Volume.Default;
		EncoderPipe = new EncoderPipe(SendCodec) { Bitrate = ScaleBitrate(config.Audio.Bitrate) };
		TimePipe = new PreciseTimedPipe(EncoderPipe.SampleInfo, id) { ReadBufferSize = EncoderPipe.PacketSize, AudioBufferLength = TimeSpan.FromMilliseconds(300) };
		MergePipe = [];

		config.Audio.Bitrate.Changed += (s, e) => EncoderPipe.Bitrate = ScaleBitrate(e.NewValue);

		MergePipe.Into(TimePipe).Chain<CheckActivePipe>().Chain(StallCheckPipe).Chain(VolumePipe).Chain(EncoderPipe);
		TimePipe.InStream = new SoftClipProducer(MergePipe);
	}

	public void SetTarget(IAudioPassiveConsumer target)
	{
		PlayerSink = target;
		EncoderPipe.Chain(target);
	}

	private static int ScaleBitrate(int value) => Math.Clamp(value, 1, 96) * 1000;

	public event AsyncEventHandler? OnSongEnd;
	public event AsyncEventHandler<SongInfoChanged>? OnSongUpdated;

	private void TriggerSongEnd(object? o, EventArgs e) => scheduler.InvokeAsync(() => OnSongEnd.InvokeAsync(this, EventArgs.Empty));
	private void TriggerSongUpdated(object? o, SongInfoChanged e) => scheduler.InvokeAsync(() => OnSongUpdated.InvokeAsync(this, e));

	public async Task Play(PlayResource res)
	{
		if (CurrentPlayerSource != null && liveGain != null)
		{
			liveGain.RampTo(0f, FadeOutHard, false);
			await WaitForRamp(liveGain, FadeOutHard);
		}
		if (res is MediaPlayResource mres && mres.IsIcyStream)
			await live.AudioStartIcy(res.PlayUri);
		else
			await live.AudioStart(res.PlayUri, res.PlayInfo?.StartOffset);
		Play(live);
	}

	/// <summary>Waits until the gain ramp has actually been applied by the audio thread instead of
	/// guessing with a wall-clock sleep. A sleep that is too short cuts the ramp off and clicks;
	/// the timeout only guards against a stalled pipeline.</summary>
	private static async Task WaitForRamp(GainProducer gain, TimeSpan duration)
	{
		var deadline = System.Environment.TickCount64 + (long)duration.TotalMilliseconds + RampGraceMs;
		while (!gain.Settled && System.Environment.TickCount64 < deadline)
			await Task.Delay(4).ConfigureAwait(false);
	}

	public void Play(IPlayerSource source)
	{
		DropIncoming(false);
		if (CurrentPlayerSource != source)
		{
			DetachLive();
			liveBuffer = new PullBuffer(source);
			liveGain = new GainProducer(liveBuffer);
			source.OnSongEnd += TriggerSongEnd;
			source.OnSongUpdated += TriggerSongUpdated;
			MergePipe.Add(liveGain);
			CurrentPlayerSource = source;
		}
		else
		{
			if (liveGain != null)
				MergePipe.Remove(liveGain);
			liveBuffer?.Dispose();
			liveBuffer = new PullBuffer(source);
			liveGain = new GainProducer(liveBuffer, 0f);
			liveGain.RampTo(1f, FadeInHard, false);
			MergePipe.Add(liveGain);
		}
		TimePipe.AudioTimer.Reset();
		TimePipe.Paused = false;
	}

	/// <summary>Starts decoding the next track into the spare decoder. Awaited rather than
	/// blocked on: spawning ffmpeg takes tens of milliseconds and used to run synchronously on
	/// the scheduler thread, which is the same thread that drives the crossfade ticks.</summary>
	public async Task BeginCrossfade(string url, TimeSpan? offset, TimeSpan duration)
	{
		DropIncoming(false);
		spare.BassDb = live.BassDb;
		spare.TrebleDb = live.TrebleDb;
		await spare.AudioStart(url, offset ?? TimeSpan.Zero).ConfigureAwait(false);
		spareBuffer = new PullBuffer(spare);
		spareGain = new GainProducer(spareBuffer, 0f);
		fadeDuration = duration < MinFade ? MinFade : duration;
		fadeArm = DateTime.UtcNow;
		incomingMixed = false;
		CrossfadeActive = true;
	}

	/// <summary>Mixes the prepared track in once it has enough audio buffered that the fade
	/// cannot run dry. Called from the tick loop, never blocks.</summary>
	public void PumpCrossfade()
	{
		if (!CrossfadeActive || incomingMixed || spareGain is null || spareBuffer is null)
			return;
		if (spareBuffer.Buffered < PrerollBytes && DateTime.UtcNow - fadeArm < PrerollTimeout)
			return;
		liveGain?.RampTo(0f, fadeDuration, true);
		spareGain.RampTo(1f, fadeDuration, true);
		MergePipe.Add(spareGain);
		incomingMixed = true;
	}

	public bool CompleteCrossfade(bool suppressFollowingEnd)
	{
		if (!CrossfadeActive || spareGain is null)
			return false;
		if (CurrentPlayerSource != null)
		{
			CurrentPlayerSource.OnSongEnd -= TriggerSongEnd;
			CurrentPlayerSource.OnSongUpdated -= TriggerSongUpdated;
		}
		if (liveGain != null)
			MergePipe.Remove(liveGain);
		liveBuffer?.Dispose();
		liveBuffer = null;
		live.AudioStop();
		if (suppressFollowingEnd)
			suppressedEnds++;
		if (!incomingMixed)
		{
			liveGain?.RampTo(0f, fadeDuration, true);
			spareGain.RampTo(1f, fadeDuration, true);
			MergePipe.Add(spareGain);
		}
		liveGain = spareGain;
		liveBuffer = spareBuffer;
		spareGain = null;
		spareBuffer = null;
		(live, spare) = (spare, live);
		live.OnSongEnd += TriggerSongEnd;
		live.OnSongUpdated += TriggerSongUpdated;
		CurrentPlayerSource = live;
		incomingMixed = false;
		CrossfadeActive = false;
		return true;
	}

	public bool TakeSuppressedEnd()
	{
		if (suppressedEnds <= 0)
			return false;
		suppressedEnds--;
		return true;
	}

	public void DropIncoming(bool restoreLive = true)
	{
		if (spareGain != null)
			MergePipe.Remove(spareGain);
		spareBuffer?.Dispose();
		spareBuffer = null;
		if (CrossfadeActive)
			spare.AudioStop();
		spareGain = null;
		incomingMixed = false;
		CrossfadeActive = false;
		if (restoreLive)
			liveGain?.RampTo(1f, FadeInHard, false);
	}

	private void DetachLive()
	{
		if (CurrentPlayerSource is null)
			return;
		CurrentPlayerSource.OnSongEnd -= TriggerSongEnd;
		CurrentPlayerSource.OnSongUpdated -= TriggerSongUpdated;
		if (liveGain != null)
			MergePipe.Remove(liveGain);
		liveBuffer?.Dispose();
		liveBuffer = null;
		CurrentPlayerSource.Dispose();
		CurrentPlayerSource = null;
		liveGain = null;
	}

	public async Task Stop()
	{
		var fading = liveGain;
		if (fading != null)
		{
			fading.RampTo(0f, FadeOutStop, false);
			await WaitForRamp(fading, FadeOutStop);
		}
		DropIncoming(false);
		// Stille nachschieben, bevor der Strom endet. Ohne sie hat der letzte
		// Opus-Frame keinen Nachfolger, mit dem der Decoder ihn auflösen kann,
		// und der Jitterpuffer des Clients läuft ohne Vorwarnung leer - der
		// Client extrapoliert den Abriss, und genau das knackt.
		//
		// Die Stille kommt in die MergePipe, nicht an PlayerSink: der hängt
		// hinter dem Encoder, dort wären PCM-Nullen ein kaputtes Opus-Paket.
		// Die Quelle wird erst danach abgebaut - ffmpeg zu töten, während der
		// Tick-Thread noch liest, war die zweite Hälfte desselben Problems.
		await SendSilenceTail();
		DetachLive();
		if (MergePipe.Count <= 1)
			TimePipe.Paused = true;
	}

	/// <summary>Schiebt nach dem Fade echte Stille nach, damit der Paketstrom
	/// ausklingt statt abzureißen. Die Dauer deckt den Opus-Overlap und den
	/// Anlauf des Client-Puffers ab - mehr würde das Stoppen nur träge machen.</summary>
	private async Task SendSilenceTail()
	{
		var silence = new SilenceProducer();
		MergePipe.Add(silence);

		try
		{
			await Task.Delay(SilenceTail);
		}
		finally
		{
			MergePipe.Remove(silence);
		}
	}

	/// <summary>Synchronous teardown for <see cref="Dispose"/>, where awaiting is not possible.
	/// Skips the fade because the pipeline is going away anyway.</summary>
	private void StopImmediate()
	{
		liveGain?.RampTo(0f, TimeSpan.Zero, false);
		DropIncoming(false);
		DetachLive();
		TimePipe.Paused = true;
	}

	public async Task StopAll()
	{
		await Stop().ConfigureAwait(false);
		TimePipe.Paused = true;
		MergePipe.Clear();
	}

	public TimeSpan? Length => CurrentPlayerSource?.Length;

	/// <summary>Position as the listener hears it. The decoder reads ahead into
	/// <see cref="PullBuffer"/>, so its raw position runs up to a second early - uncorrected,
	/// every crossfade and every song-end decision fired too soon.</summary>
	public TimeSpan? Position
	{
		get
		{
			var raw = CurrentPlayerSource?.Position;
			if (raw is null)
				return null;
			var ahead = liveBuffer?.BufferedDuration ?? TimeSpan.Zero;
			var corrected = raw.Value - ahead;
			return corrected < TimeSpan.Zero ? TimeSpan.Zero : corrected;
		}
	}

	public async Task Seek(TimeSpan position)
	{
		var gain = liveGain;
		if (gain != null)
		{
			gain.RampTo(0f, FadeOutHard, false);
			await WaitForRamp(gain, FadeOutHard).ConfigureAwait(false);
		}
		if (CurrentPlayerSource != null)
			await CurrentPlayerSource.Seek(position);
		liveBuffer?.Clear();
		gain?.RampTo(1f, FadeInHard, false);
	}

	public float Volume
	{
		get => AudioValues.FactorToHumanVolume(VolumePipe.Volume);
		set => VolumePipe.Volume = AudioValues.HumanVolumeToFactor(value);
	}

	public bool Paused
	{
		get => TimePipe.Paused;
		set => TimePipe.Paused = value;
	}

	// Extras

	public void SetStall() => StallCheckPipe.SetStall();

	[Obsolete(AttributeStrings.UnderDevelopment)]
	public void MixInStreamOnce(IPlayerSource producer)
	{
		producer.OnSongEnd += (s, e) =>
		{
			MergePipe.Remove(producer);
			producer.Dispose();
		};
		MergePipe.Add(producer);
		TimePipe.Paused = false;
	}

	public void Dispose()
	{
		StopImmediate();
		MergePipe.Clear();
		TimePipe.Dispose();
		decoderA.Dispose();
		decoderB.Dispose();
		EncoderPipe.Dispose();
	}
}

/// <summary>
/// Applies a sample-accurate gain ramp to a stereo stream. Lock-free on the audio path: a ramp
/// request is published as one immutable object, the reader picks it up on its next block.
/// </summary>
internal sealed class GainProducer : IAudioPassiveProducer
{
	private const int SampleRate = 48000;

	private sealed record Ramp(float Origin, float Destination, int Samples, bool EqualPower);

	private readonly IAudioPassiveProducer inner;
	private Ramp? requested;
	private Ramp active;
	private int rampPos;
	private volatile float applied;

	public GainProducer(IAudioPassiveProducer inner, float start = 1f)
	{
		this.inner = inner;
		applied = start;
		active = new Ramp(start, start, 1, false);
		rampPos = 1;
	}

	/// <summary>True once the ramp has fully been applied to audio that was handed downstream.</summary>
	public bool Settled => Volatile.Read(ref requested) is null && rampPos >= active.Samples;

	public void RampTo(float to, TimeSpan duration, bool power)
	{
		var samples = Math.Max(1, (int)(duration.TotalSeconds * SampleRate));
		// Origin is the gain that was last written to audio, so a ramp interrupted halfway
		// continues from where it audibly is instead of jumping.
		Volatile.Write(ref requested, new Ramp(applied, Math.Clamp(to, 0f, 1f), samples, power));
	}

	public int Read(Span<byte> data, out Meta? meta)
	{
		var read = inner.Read(data, out meta);
		if (read <= 0)
			return read;

		var pending = Volatile.Read(ref requested);
		if (pending is not null)
		{
			active = pending;
			rampPos = 0;
			Volatile.Write(ref requested, null);
		}

		var samples = MemoryMarshal.Cast<byte, short>(data[..(read - (read % 4))]);
		var ramp = active;
		var pos = rampPos;
		var total = ramp.Samples;
		var gain = applied;

		if (pos >= total && ramp.Destination == 1f)
		{
			// Unity gain and nothing to ramp: leave the samples untouched.
			applied = 1f;
			return read;
		}

		for (var i = 0; i + 1 < samples.Length; i += 2)
		{
			gain = pos >= total ? ramp.Destination : GainAt(ramp, pos / (float)total);
			if (pos < total)
				pos++;
			samples[i] = Scale(samples[i], gain);
			samples[i + 1] = Scale(samples[i + 1], gain);
		}

		rampPos = pos;
		applied = gain;
		return read;
	}

	private static float GainAt(Ramp ramp, float t)
	{
		if (!ramp.EqualPower)
			return ramp.Origin + ((ramp.Destination - ramp.Origin) * t);

		// Equal-power curve: interpolate power linearly, then take the square root for amplitude.
		// Two tracks crossing with complementary ramps then sum to constant power - fade-out
		// gives sqrt(1-t), fade-in sqrt(t), and (1-t) + t == 1 at every point.
		// The previous version interpolated amplitude and subtracted a fixed 8 percent in the
		// middle, which was an audible dip on every transition.
		var from = ramp.Origin * ramp.Origin;
		var to = ramp.Destination * ramp.Destination;
		return MathF.Sqrt(from + ((to - from) * t));
	}

	private static short Scale(short sample, float gain)
		=> (short)Math.Clamp((int)MathF.Round(sample * gain), short.MinValue, short.MaxValue);
}

/// <summary>
/// Catches the peaks that summing two tracks during a crossfade can produce, with a smooth
/// tanh knee instead of the hard clamp the mixer would otherwise apply. Below the threshold the
/// signal is bit-identical, so normal playback is never coloured.
/// </summary>
internal sealed class SoftClipProducer : IAudioPassiveProducer
{
	/// <summary>Knee start, -1 dBFS. Below this nothing is touched at all.</summary>
	private const float Threshold = 29204f;
	private const float Ceiling = 32767f;
	private const float Headroom = Ceiling - Threshold;

	private readonly IAudioPassiveProducer inner;

	public SoftClipProducer(IAudioPassiveProducer inner)
	{
		this.inner = inner;
	}

	public int Read(Span<byte> data, out Meta? meta)
	{
		var read = inner.Read(data, out meta);
		if (read <= 0)
			return read;
		var samples = MemoryMarshal.Cast<byte, short>(data[..(read - (read % 2))]);
		for (var i = 0; i < samples.Length; i++)
		{
			var value = samples[i];
			// short.MinValue has no positive counterpart, so Math.Abs on it throws - that
			// overflow used to kill the whole process from the audio thread.
			var abs = value == short.MinValue ? 32768f : MathF.Abs(value);
			if (abs <= Threshold)
				continue;
			// tanh maps the excess asymptotically into the remaining headroom: continuous in
			// value and slope at the threshold, and it can never exceed the ceiling.
			var eased = Threshold + (Headroom * MathF.Tanh((abs - Threshold) / Headroom));
			samples[i] = (short)(value < 0 ? -eased : eased);
		}
		return read;
	}
}

/// <summary>
/// Decouples a blocking producer (ffmpeg stdout) from the audio thread through a
/// single-producer/single-consumer ring buffer. The consumer never takes a lock and never
/// waits, so a slow or stalling decoder can no longer delay a send tick.
/// Both sides own exactly one cursor; the other side only ever reads it with release/acquire
/// semantics, which is what makes the design safe without a monitor.
/// </summary>
internal sealed class PullBuffer : IAudioPassiveProducer, IDisposable
{
	private const int Capacity = 1 << 18; // 262144 B, about 1.36 s at 48 kHz stereo 16 bit
	private const int Mask = Capacity - 1;
	private const int FrameBytes = 4; // 16 bit stereo
	private const int ChunkBytes = 8192;
	private const int BytesPerSecond = 48000 * 2 * 2;

	private readonly IAudioPassiveProducer inner;
	private readonly byte[] fifo = new byte[Capacity];
	private readonly Thread pump;

	private long writePos; // written by the pump thread only
	private long readPos; // written by the consumer only
	private int clearPending;
	private volatile bool closed;

	public PullBuffer(IAudioPassiveProducer inner)
	{
		this.inner = inner;
		pump = new Thread(Pump) { IsBackground = true, Name = "hostleaf-audio" };
		pump.Start();
	}

	/// <summary>Drops everything buffered. Applied by the consumer on its next read so that
	/// no caller can ever stall the audio thread by clearing.</summary>
	public void Clear() => Volatile.Write(ref clearPending, 1);

	public int Buffered
	{
		get
		{
			var pending = Volatile.Read(ref writePos) - Volatile.Read(ref readPos);
			return pending <= 0 ? 0 : (int)Math.Min(pending, Capacity);
		}
	}

	/// <summary>How far the decoder runs ahead of what the listener actually hears.
	/// Used to correct the reported song position, otherwise every crossfade would start early.</summary>
	public TimeSpan BufferedDuration => TimeSpan.FromSeconds(Buffered / (double)BytesPerSecond);

	public int Read(Span<byte> data, out Meta? meta)
	{
		meta = null;
		var write = Volatile.Read(ref writePos);

		if (Volatile.Read(ref clearPending) != 0)
		{
			Volatile.Write(ref readPos, write);
			Volatile.Write(ref clearPending, 0);
			return 0;
		}

		var read = readPos;
		var available = (int)Math.Min(write - read, Capacity);
		var n = Math.Min(data.Length, available);
		n -= n % FrameBytes;
		if (n <= 0)
			return 0;

		var start = (int)(read & Mask);
		var first = Math.Min(n, Capacity - start);
		fifo.AsSpan(start, first).CopyTo(data);
		if (n > first)
			fifo.AsSpan(0, n - first).CopyTo(data[first..]);

		Volatile.Write(ref readPos, read + n);
		return n;
	}

	public void Dispose() => closed = true;

	private void Pump()
	{
		var scratch = new byte[ChunkBytes];
		var carry = 0; // bytes of an incomplete frame, kept at the front of scratch

		while (!closed)
		{
			int got;
			try
			{
				got = inner.Read(scratch.AsSpan(carry, ChunkBytes - carry), out _);
			}
			catch (Exception)
			{
				break;
			}
			if (got <= 0)
			{
				Thread.Sleep(1);
				continue;
			}

			var total = carry + got;
			carry = total % FrameBytes;
			var whole = total - carry;
			if (whole <= 0)
				continue;

			Append(scratch.AsSpan(0, whole));
			if (carry > 0)
				scratch.AsSpan(whole, carry).CopyTo(scratch);
		}
	}

	private void Append(ReadOnlySpan<byte> source)
	{
		var offset = 0;
		while (offset < source.Length && !closed)
		{
			var write = writePos;
			var free = (int)(Capacity - (write - Volatile.Read(ref readPos)));
			if (free <= 0)
			{
				Thread.Sleep(1);
				continue;
			}
			var take = Math.Min(free, source.Length - offset);
			var start = (int)(write & Mask);
			var first = Math.Min(take, Capacity - start);
			source.Slice(offset, first).CopyTo(fifo.AsSpan(start, first));
			if (take > first)
				source.Slice(offset + first, take - first).CopyTo(fifo);
			Volatile.Write(ref writePos, write + take);
			offset += take;
		}
	}
}

/// <summary>Liefert Stille, solange sie gelesen wird.
///
/// Gebraucht beim Stoppen: der Paketstrom soll auf echter Stille auslaufen statt
/// mitten in der Welle abzureißen. Ein Producer in der MergePipe ist der
/// einzige Weg dorthin, der durch Encoder und Timer läuft wie normaler Ton.</summary>
internal sealed class SilenceProducer : IAudioPassiveProducer
{
	public int Read(Span<byte> data, out Meta? meta)
	{
		meta = null;
		data.Clear();

		return data.Length;
	}
}
