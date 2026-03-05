using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace TerrariaApi.Server
{
	internal static class RuntimeMetrics
	{
		private sealed class PacketCounter
		{
			public long Count;
			public long TotalTimestampTicks;
			public long PreTimestampTicks;
			public long HookTimestampTicks;
			public long MaxTimestampTicks;
		}

		private sealed class HookCounter
		{
			public long Count;
			public long TotalTimestampTicks;
			public long MaxTimestampTicks;
		}

		private sealed class PacketSnapshot
		{
			public int PacketId;
			public long Count;
			public long TotalTimestampTicks;
			public long PreTimestampTicks;
			public long HookTimestampTicks;
			public long MaxTimestampTicks;
		}

		private sealed class HookSnapshot
		{
			public string Name = string.Empty;
			public long Count;
			public long TotalTimestampTicks;
			public long MaxTimestampTicks;
		}

		private static readonly PacketCounter[] packetCounters = CreatePacketCounters();
		private static readonly ConcurrentDictionary<string, HookCounter> hookCounters =
			new ConcurrentDictionary<string, HookCounter>(StringComparer.Ordinal);
		private static readonly object lifecycleLock = new object();

		private static Timer snapshotTimer;
		private static int snapshotIntervalSeconds = 60;
		private static int topEntries = 10;
		private static volatile bool enabled;
		private static int snapshotQueued;

		public static bool Enabled => enabled;

		public static void Configure(bool runtimeProfileEnabled, int intervalSeconds, int topN)
		{
			lock (lifecycleLock)
			{
				enabled = runtimeProfileEnabled;
				snapshotIntervalSeconds = Math.Clamp(intervalSeconds, 5, 3600);
				topEntries = Math.Clamp(topN, 1, 30);

				snapshotTimer?.Dispose();
				snapshotTimer = null;
				ClearCounters();

				if (!enabled)
					return;

				snapshotTimer = new Timer(
					static _ => QueueSnapshot(),
					null,
					TimeSpan.FromSeconds(snapshotIntervalSeconds),
					TimeSpan.FromSeconds(snapshotIntervalSeconds));
			}

			ServerApi.LogWriter.ServerWriteLine(
				string.Format(
					"Runtime profiling enabled. Interval={0}s Top={1}.",
					snapshotIntervalSeconds,
					topEntries),
				TraceLevel.Info);
		}

		public static void Shutdown()
		{
			EmitSnapshotSafe();

			lock (lifecycleLock)
			{
				enabled = false;
				snapshotTimer?.Dispose();
				snapshotTimer = null;
				Interlocked.Exchange(ref snapshotQueued, 0);
				ClearCounters();
			}
		}

		public static void RecordNetPacket(byte packetId, long totalTimestampTicks, long preTimestampTicks, long hookTimestampTicks)
		{
			if (!enabled)
				return;

			PacketCounter counter = packetCounters[packetId];
			Interlocked.Increment(ref counter.Count);
			Interlocked.Add(ref counter.TotalTimestampTicks, totalTimestampTicks);
			Interlocked.Add(ref counter.PreTimestampTicks, preTimestampTicks);
			Interlocked.Add(ref counter.HookTimestampTicks, hookTimestampTicks);
			UpdateMax(ref counter.MaxTimestampTicks, totalTimestampTicks);
		}

		public static void RecordHookHandler(string pluginName, string hookName, long elapsedTimestampTicks)
		{
			if (!enabled || elapsedTimestampTicks <= 0)
				return;

			string key = string.Concat(pluginName, "/", hookName);
			HookCounter counter = hookCounters.GetOrAdd(key, static _ => new HookCounter());
			Interlocked.Increment(ref counter.Count);
			Interlocked.Add(ref counter.TotalTimestampTicks, elapsedTimestampTicks);
			UpdateMax(ref counter.MaxTimestampTicks, elapsedTimestampTicks);
		}

		private static void EmitSnapshotSafe()
		{
			try
			{
				EmitSnapshot();
			}
			catch (Exception ex)
			{
				ServerApi.LogWriter.ServerWriteLine(
					string.Format("Runtime profiling snapshot failed: {0}", ex),
					TraceLevel.Warning);
			}
		}

		internal static void RunMaintenance()
		{
			int removed = 0;
			foreach (KeyValuePair<string, HookCounter> pair in hookCounters)
			{
				HookCounter counter = pair.Value;
				if (Volatile.Read(ref counter.Count) != 0
					|| Volatile.Read(ref counter.TotalTimestampTicks) != 0
					|| Volatile.Read(ref counter.MaxTimestampTicks) != 0)
				{
					continue;
				}

				if (hookCounters.TryRemove(pair.Key, out _))
					removed++;
			}

			if (removed > 0)
			{
				ServerApi.LogWriter.ServerWriteLine(
					$"Runtime metrics maintenance removed {removed} inactive hook counters.",
					TraceLevel.Verbose);
			}
		}

		private static void QueueSnapshot()
		{
			if (!enabled)
				return;

			if (Interlocked.Exchange(ref snapshotQueued, 1) != 0)
				return;

			if (!BackgroundWork.TryQueue("runtime-metrics-snapshot", RunSnapshotFromBackground))
			{
				try
				{
					EmitSnapshotSafe();
				}
				finally
				{
					Interlocked.Exchange(ref snapshotQueued, 0);
				}
			}
		}

		private static void RunSnapshotFromBackground()
		{
			try
			{
				EmitSnapshotSafe();
			}
			finally
			{
				Interlocked.Exchange(ref snapshotQueued, 0);
			}
		}

		private static void EmitSnapshot()
		{
			if (!enabled)
				return;

			List<PacketSnapshot> packetSnapshots = CapturePacketSnapshots();
			List<HookSnapshot> hookSnapshots = CaptureHookSnapshots();

			if (packetSnapshots.Count == 0 && hookSnapshots.Count == 0)
				return;

			packetSnapshots.Sort(static (left, right) => right.TotalTimestampTicks.CompareTo(left.TotalTimestampTicks));
			hookSnapshots.Sort(static (left, right) => right.TotalTimestampTicks.CompareTo(left.TotalTimestampTicks));

			ServerApi.LogWriter.ServerWriteLine(
				string.Format("[RuntimeProfile] Snapshot over last {0}s:", snapshotIntervalSeconds),
				TraceLevel.Info);

			foreach (PacketSnapshot snapshot in packetSnapshots.Take(topEntries))
			{
				string packetName = Enum.IsDefined(typeof(PacketTypes), snapshot.PacketId)
					? ((PacketTypes)snapshot.PacketId).ToString()
					: string.Format("Unknown({0})", snapshot.PacketId);

				double totalMs = ToMilliseconds(snapshot.TotalTimestampTicks);
				double avgMs = snapshot.Count > 0 ? totalMs / snapshot.Count : 0;
				double maxMs = ToMilliseconds(snapshot.MaxTimestampTicks);
				double preMs = ToMilliseconds(snapshot.PreTimestampTicks);
				double hookMs = ToMilliseconds(snapshot.HookTimestampTicks);

				ServerApi.LogWriter.ServerWriteLine(
					string.Format(
						"[RuntimeProfile] packet={0} count={1} total={2:F3}ms avg={3:F4}ms max={4:F4}ms pre={5:F3}ms hooks={6:F3}ms",
						packetName,
						snapshot.Count,
						totalMs,
						avgMs,
						maxMs,
						preMs,
						hookMs),
					TraceLevel.Info);
			}

			foreach (HookSnapshot snapshot in hookSnapshots.Take(topEntries))
			{
				double totalMs = ToMilliseconds(snapshot.TotalTimestampTicks);
				double avgMs = snapshot.Count > 0 ? totalMs / snapshot.Count : 0;
				double maxMs = ToMilliseconds(snapshot.MaxTimestampTicks);

				ServerApi.LogWriter.ServerWriteLine(
					string.Format(
						"[RuntimeProfile] hook={0} count={1} total={2:F3}ms avg={3:F4}ms max={4:F4}ms",
						snapshot.Name,
						snapshot.Count,
						totalMs,
						avgMs,
						maxMs),
					TraceLevel.Info);
			}
		}

		private static List<PacketSnapshot> CapturePacketSnapshots()
		{
			var snapshots = new List<PacketSnapshot>();

			for (int i = 0; i < packetCounters.Length; i++)
			{
				PacketCounter counter = packetCounters[i];
				long count = Interlocked.Exchange(ref counter.Count, 0);
				if (count == 0)
					continue;

				snapshots.Add(new PacketSnapshot
				{
					PacketId = i,
					Count = count,
					TotalTimestampTicks = Interlocked.Exchange(ref counter.TotalTimestampTicks, 0),
					PreTimestampTicks = Interlocked.Exchange(ref counter.PreTimestampTicks, 0),
					HookTimestampTicks = Interlocked.Exchange(ref counter.HookTimestampTicks, 0),
					MaxTimestampTicks = Interlocked.Exchange(ref counter.MaxTimestampTicks, 0),
				});
			}

			return snapshots;
		}

		private static List<HookSnapshot> CaptureHookSnapshots()
		{
			var snapshots = new List<HookSnapshot>();

			foreach (KeyValuePair<string, HookCounter> pair in hookCounters)
			{
				HookCounter counter = pair.Value;
				long count = Interlocked.Exchange(ref counter.Count, 0);
				if (count == 0)
					continue;

				snapshots.Add(new HookSnapshot
				{
					Name = pair.Key,
					Count = count,
					TotalTimestampTicks = Interlocked.Exchange(ref counter.TotalTimestampTicks, 0),
					MaxTimestampTicks = Interlocked.Exchange(ref counter.MaxTimestampTicks, 0),
				});
			}

			return snapshots;
		}

		private static void ClearCounters()
		{
			foreach (PacketCounter counter in packetCounters)
			{
				Interlocked.Exchange(ref counter.Count, 0);
				Interlocked.Exchange(ref counter.TotalTimestampTicks, 0);
				Interlocked.Exchange(ref counter.PreTimestampTicks, 0);
				Interlocked.Exchange(ref counter.HookTimestampTicks, 0);
				Interlocked.Exchange(ref counter.MaxTimestampTicks, 0);
			}

			foreach (KeyValuePair<string, HookCounter> pair in hookCounters)
			{
				HookCounter counter = pair.Value;
				Interlocked.Exchange(ref counter.Count, 0);
				Interlocked.Exchange(ref counter.TotalTimestampTicks, 0);
				Interlocked.Exchange(ref counter.MaxTimestampTicks, 0);
			}
		}

		private static double ToMilliseconds(long timestampTicks)
		{
			return timestampTicks * 1000d / Stopwatch.Frequency;
		}

		private static PacketCounter[] CreatePacketCounters()
		{
			var counters = new PacketCounter[byte.MaxValue + 1];
			for (int i = 0; i < counters.Length; i++)
			{
				counters[i] = new PacketCounter();
			}

			return counters;
		}

		private static void UpdateMax(ref long destination, long value)
		{
			long current = Volatile.Read(ref destination);
			while (value > current)
			{
				long original = Interlocked.CompareExchange(ref destination, value, current);
				if (original == current)
					return;

				current = original;
			}
		}
	}
}
