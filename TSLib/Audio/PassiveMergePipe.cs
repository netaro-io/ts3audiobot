// TSLib - A free TeamSpeak 3 and 5 client library
// Copyright (C) 2017  TSLib contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace TSLib.Audio;

public class PassiveMergePipe : IAudioPassiveProducer, ICollection<IAudioPassiveProducer>
{
	private IAudioPassiveProducer[] safeProducerList = [];
	private readonly List<IAudioPassiveProducer> producerList = [];
	private readonly Lock listLock = new();
	private bool changed;
	private int[] accBuffer = [];
	private byte[] mixScratch = [];

	public int Count => safeProducerList.Length;
	public bool IsReadOnly => false;

	public void Add(IAudioPassiveProducer addProducer)
	{
		if (!producerList.Contains(addProducer) && addProducer != this)
		{
			lock (listLock)
			{
				if (!producerList.Contains(addProducer))
				{
					producerList.Add(addProducer);
					changed = true;
				}
			}
		}
	}

	public bool Remove(IAudioPassiveProducer removeProducer)
	{
		if (producerList.Contains(removeProducer) && removeProducer != this)
		{
			lock (listLock)
			{
				var removed = producerList.Remove(removeProducer);
				changed |= removed;
				return removed;
			}
		}
		return false;
	}

	public void Clear()
	{
		lock (listLock)
		{
			producerList.Clear();
			changed = true;
		}
	}

	public int Read(Span<byte> data, out Meta? meta)
	{
		if (changed)
		{
			lock (listLock)
			{
				if (changed)
				{
					safeProducerList = producerList.ToArray();
					changed = false;
				}
			}
		}

		meta = null;

		if (safeProducerList.Length == 0)
			return 0;

		if (safeProducerList.Length == 1)
			return safeProducerList[0].Read(data, out _);

		// Every producer reads into its own scratch span. Reading them all into `data` and
		// summing in place would add the previous producer's tail a second time whenever a
		// producer returns fewer bytes than the one before it - which is exactly what happens
		// while a crossfade is still filling up, and it was audible as a click.
		int maxReadLength = data.Length - (data.Length % 4);
		if (maxReadLength <= 0)
			return 0;

		if (mixScratch.Length < maxReadLength)
			mixScratch = new byte[maxReadLength];
		if (accBuffer.Length < maxReadLength / 2)
			accBuffer = new int[maxReadLength / 2];

		var acc = accBuffer.AsSpan(0, maxReadLength / 2);
		acc.Clear();

		var scratch = mixScratch.AsSpan(0, maxReadLength);
		var scratchPcm = MemoryMarshal.Cast<byte, short>(scratch);
		int read = 0;

		foreach (var producer in safeProducerList)
		{
			int ppread = producer.Read(scratch, out var producerMeta);
			if (ppread <= 0)
				continue;
			ppread -= ppread % 2;
			if (ppread <= 0)
				continue;

			meta ??= producerMeta;
			read = Math.Max(read, ppread);
			var samples = scratchPcm[..(ppread / 2)];
			for (int i = 0; i < samples.Length; i++)
				acc[i] += samples[i];
		}

		if (read == 0)
			return 0;

		var pcmBuffer = MemoryMarshal.Cast<byte, short>(data);
		for (int i = 0; i < read / 2; i++)
			pcmBuffer[i] = (short)Math.Clamp(acc[i], short.MinValue, short.MaxValue);

		return read;
	}

	IEnumerator<IAudioPassiveProducer> IEnumerable<IAudioPassiveProducer>.GetEnumerator() => ((IEnumerable<IAudioPassiveProducer>)safeProducerList).GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => safeProducerList.GetEnumerator();

	public bool Contains(IAudioPassiveProducer item) => safeProducerList.Contains(item);

	public void CopyTo(IAudioPassiveProducer[] array, int arrayIndex) => Array.Copy(safeProducerList, 0, array, arrayIndex, array.Length);
}
