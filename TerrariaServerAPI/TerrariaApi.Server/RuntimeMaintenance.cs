using System;
using System.Diagnostics;
using System.Threading;

namespace TerrariaApi.Server
{
	internal static class RuntimeMaintenance
	{
		private static readonly object lifecycleLock = new object();
		private static Timer maintenanceTimer;
		private static volatile bool enabled;
		private static int maintenanceQueued;

		public static void Configure(RuntimeExecutionSettings settings)
		{
			if (settings is null)
				throw new ArgumentNullException(nameof(settings));

			lock (lifecycleLock)
			{
				ShutdownLocked();

				if (!settings.EnableMaintenance)
				{
					enabled = false;
					return;
				}

				enabled = true;
				TimeSpan interval = TimeSpan.FromSeconds(settings.MaintenanceIntervalSeconds);
				maintenanceTimer = new Timer(
					static _ => QueueMaintenance(),
					null,
					interval,
					interval);
			}

			ServerApi.LogWriter.ServerWriteLine(
				$"Runtime maintenance enabled. Interval={settings.MaintenanceIntervalSeconds}s.",
				TraceLevel.Info);
		}

		public static void Shutdown()
		{
			lock (lifecycleLock)
			{
				ShutdownLocked();
			}
		}

		private static void QueueMaintenance()
		{
			if (!enabled)
				return;

			if (Interlocked.Exchange(ref maintenanceQueued, 1) != 0)
				return;

			if (!BackgroundWork.TryQueue("runtime-maintenance", RunMaintenance))
			{
				ThreadPool.UnsafeQueueUserWorkItem(
					static _ => RunMaintenance(),
					null);
			}
		}

		private static void RunMaintenance()
		{
			try
			{
				RuntimeMetrics.RunMaintenance();
				BackgroundWork.RunMaintenance();
			}
			catch (Exception ex)
			{
				ServerApi.LogWriter.ServerWriteLine(
					$"Runtime maintenance failed: {ex}",
					TraceLevel.Warning);
			}
			finally
			{
				Interlocked.Exchange(ref maintenanceQueued, 0);
			}
		}

		private static void ShutdownLocked()
		{
			enabled = false;
			maintenanceTimer?.Dispose();
			maintenanceTimer = null;
			Interlocked.Exchange(ref maintenanceQueued, 0);
		}
	}
}
