using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerrariaApi.Server
{
	internal sealed class RuntimeExecutionSettings
	{
		private const int MinWorkerThreads = 1;
		private const int MaxWorkerThreads = 16;
		private const int MinQueueLimit = 256;
		private const int MaxQueueLimit = 65536;
		private const int MinMainThreadDrain = 1;
		private const int MaxMainThreadDrain = 4096;
		private const int MinMaintenanceIntervalSeconds = 1;
		private const int MaxMaintenanceIntervalSeconds = 300;

		[JsonPropertyName("enable")]
		public bool EnableBackgroundWork { get; set; } = true;

		[JsonPropertyName("worker_threads")]
		public int WorkerThreads { get; set; } = 4;

		[JsonPropertyName("queue_limit")]
		public int QueueLimit { get; set; } = 8192;

		[JsonPropertyName("main_thread_drain_per_tick")]
		public int MainThreadDrainPerTick { get; set; } = 256;

		[JsonPropertyName("maintenance_enable")]
		public bool EnableMaintenance { get; set; } = true;

		[JsonPropertyName("interval")]
		public int MaintenanceIntervalSeconds { get; set; } = 15;

		public static RuntimeExecutionSettings LoadOrCreate(string configPath)
		{
			RuntimeExecutionSettings settings;
			if (!File.Exists(configPath))
			{
				settings = new RuntimeExecutionSettings();
				settings.Normalize();
				Save(configPath, settings);
				return settings;
			}

			try
			{
				settings = JsonSerializer.Deserialize<RuntimeExecutionSettings>(File.ReadAllText(configPath))
					?? new RuntimeExecutionSettings();
				settings.Normalize();
				return settings;
			}
			catch (Exception ex)
			{
				ServerApi.LogWriter.ServerWriteLine(
					$"Failed to read runtime settings from \"{configPath}\". Using defaults. {ex.Message}",
					TraceLevel.Warning);
				settings = new RuntimeExecutionSettings();
				settings.Normalize();
				return settings;
			}
		}

		private static void Save(string configPath, RuntimeExecutionSettings settings)
		{
			try
			{
				string payload = JsonSerializer.Serialize(settings, new JsonSerializerOptions
				{
					WriteIndented = true
				});
				File.WriteAllText(configPath, payload);
			}
			catch (Exception ex)
			{
				ServerApi.LogWriter.ServerWriteLine(
					$"Failed to write default runtime settings to \"{configPath}\": {ex.Message}",
					TraceLevel.Warning);
			}
		}

		private void Normalize()
		{
			WorkerThreads = Math.Clamp(WorkerThreads, MinWorkerThreads, MaxWorkerThreads);
			QueueLimit = Math.Clamp(QueueLimit, MinQueueLimit, MaxQueueLimit);
			MainThreadDrainPerTick = Math.Clamp(MainThreadDrainPerTick, MinMainThreadDrain, MaxMainThreadDrain);
			MaintenanceIntervalSeconds = Math.Clamp(
				MaintenanceIntervalSeconds,
				MinMaintenanceIntervalSeconds,
				MaxMaintenanceIntervalSeconds);
		}
	}
}
