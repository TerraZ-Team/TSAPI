using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace TerrariaApi.Server
{
	internal static class BackgroundWork
	{
		private readonly struct WorkItem
		{
			public WorkItem(string name, Action action)
			{
				Name = name;
				Action = action;
			}

			public string Name { get; }
			public Action Action { get; }
		}

		private static readonly object lifecycleLock = new object();
		private static readonly ConcurrentQueue<Action> pendingMainThreadActions = new ConcurrentQueue<Action>();

		private static BlockingCollection<WorkItem> pendingWorkItems;
		private static CancellationTokenSource cancellationTokenSource;
		private static List<Thread> workers = new List<Thread>();
		private static volatile bool enabled;
		private static int pendingMainThreadCount;
		private static int maxMainThreadQueueSize = 8192;
		private static int mainThreadDrainPerTick = 256;
		private static int droppedBackgroundWorkItems;
		private static int droppedMainThreadActions;

		public static bool Enabled => enabled;

		public static void Configure(RuntimeExecutionSettings settings)
		{
			if (settings is null)
				throw new ArgumentNullException(nameof(settings));

			lock (lifecycleLock)
			{
				StopLocked();

				maxMainThreadQueueSize = settings.QueueLimit;
				mainThreadDrainPerTick = settings.MainThreadDrainPerTick;

				if (!settings.EnableBackgroundWork)
				{
					enabled = false;
					ServerApi.LogWriter.ServerWriteLine(
						"Background workers are disabled by runtime settings.",
						TraceLevel.Info);
					return;
				}

				cancellationTokenSource = new CancellationTokenSource();
				pendingWorkItems = new BlockingCollection<WorkItem>(settings.QueueLimit);
				workers = new List<Thread>(settings.WorkerThreads);

				for (int i = 0; i < settings.WorkerThreads; i++)
				{
					var worker = new Thread(WorkerLoop)
					{
						IsBackground = true,
						Name = $"TSAPI-BG-{i + 1}"
					};
					workers.Add(worker);
					worker.Start();
				}

				enabled = true;
			}

			ServerApi.LogWriter.ServerWriteLine(
				$"Background workers started: workers={settings.WorkerThreads}, queue_limit={settings.QueueLimit}, main_thread_drain={settings.MainThreadDrainPerTick}.",
				TraceLevel.Info);
		}

		public static bool TryQueue(string name, Action action)
		{
			if (action is null)
				throw new ArgumentNullException(nameof(action));

			BlockingCollection<WorkItem> queue = pendingWorkItems;
			if (!enabled || queue is null || queue.IsAddingCompleted)
				return false;

			if (queue.TryAdd(new WorkItem(string.IsNullOrWhiteSpace(name) ? "unnamed" : name, action)))
				return true;

			int dropped = Interlocked.Increment(ref droppedBackgroundWorkItems);
			if ((dropped & 0xFF) == 1)
			{
				ServerApi.LogWriter.ServerWriteLine(
					$"Background queue is full. Dropped jobs={dropped}.",
					TraceLevel.Warning);
			}

			return false;
		}

		public static bool TryQueueMainThread(Action action)
		{
			if (action is null)
				throw new ArgumentNullException(nameof(action));

			int queueSize = Interlocked.Increment(ref pendingMainThreadCount);
			if (queueSize <= maxMainThreadQueueSize)
			{
				pendingMainThreadActions.Enqueue(action);
				return true;
			}

			Interlocked.Decrement(ref pendingMainThreadCount);
			int dropped = Interlocked.Increment(ref droppedMainThreadActions);
			if ((dropped & 0xFF) == 1)
			{
				ServerApi.LogWriter.ServerWriteLine(
					$"Main-thread callback queue is full. Dropped callbacks={dropped}.",
					TraceLevel.Warning);
			}

			return false;
		}

		public static int DrainMainThreadQueue()
		{
			return DrainMainThreadQueue(mainThreadDrainPerTick);
		}

		public static int DrainMainThreadQueue(int maxItems)
		{
			if (maxItems <= 0)
				return 0;

			int processed = 0;
			while (processed < maxItems && pendingMainThreadActions.TryDequeue(out Action action))
			{
				Interlocked.Decrement(ref pendingMainThreadCount);
				try
				{
					action();
				}
				catch (Exception ex)
				{
					ServerApi.LogWriter.ServerWriteLine(
						$"Main-thread callback failed: {ex}",
						TraceLevel.Warning);
				}

				processed++;
			}

			return processed;
		}

		public static void RunMaintenance()
		{
			int droppedWork = Interlocked.Exchange(ref droppedBackgroundWorkItems, 0);
			int droppedMainThread = Interlocked.Exchange(ref droppedMainThreadActions, 0);
			if (droppedWork > 0 || droppedMainThread > 0)
			{
				ServerApi.LogWriter.ServerWriteLine(
					$"Background maintenance: dropped_work={droppedWork}, dropped_main_thread_callbacks={droppedMainThread}.",
					TraceLevel.Warning);
			}
		}

		public static void Shutdown()
		{
			lock (lifecycleLock)
			{
				StopLocked();
			}
		}

		private static void WorkerLoop()
		{
			CancellationToken token = cancellationTokenSource.Token;
			BlockingCollection<WorkItem> queue = pendingWorkItems;
			while (!token.IsCancellationRequested)
			{
				WorkItem item;
				try
				{
					item = queue.Take(token);
				}
				catch (OperationCanceledException)
				{
					break;
				}
				catch (InvalidOperationException)
				{
					break;
				}

				try
				{
					item.Action();
				}
				catch (Exception ex)
				{
					ServerApi.LogWriter.ServerWriteLine(
						$"Background job \"{item.Name}\" failed: {ex}",
						TraceLevel.Warning);
				}
			}
		}

		private static void StopLocked()
		{
			enabled = false;

			pendingWorkItems?.CompleteAdding();
			cancellationTokenSource?.Cancel();

			foreach (Thread worker in workers)
			{
				if (worker.IsAlive)
					worker.Join(TimeSpan.FromSeconds(1));
			}

			workers.Clear();
			pendingWorkItems?.Dispose();
			pendingWorkItems = null;
			cancellationTokenSource?.Dispose();
			cancellationTokenSource = null;

			while (pendingMainThreadActions.TryDequeue(out _))
				Interlocked.Decrement(ref pendingMainThreadCount);

			Interlocked.Exchange(ref pendingMainThreadCount, 0);
			Interlocked.Exchange(ref droppedBackgroundWorkItems, 0);
			Interlocked.Exchange(ref droppedMainThreadActions, 0);
		}
	}
}
