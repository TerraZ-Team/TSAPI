using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace TerrariaApi.Server
{
	public class ServerLogWriter : ILogWriter, IDisposable
	{
		private const int FileBufferSize = 64 * 1024;
		private const int MaxBatchSize = 256;
		private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
		private static readonly object consoleWriteLock = new object();

		protected StreamWriter LogFileWriter { get; private set; }
		private readonly ConcurrentQueue<string> pendingFileLines = new ConcurrentQueue<string>();
		private readonly AutoResetEvent pendingSignal = new AutoResetEvent(false);
		private readonly Thread fileWriterThread;
		private volatile bool disposeRequested;
		private int disposed;

		public string Name
		{
			get { return "Server Log Writer"; }
		}

		public ServerLogWriter(string logFilePath = "ServerLog.txt")
		{
			try
			{
				this.LogFileWriter = new StreamWriter(logFilePath, true, Encoding.UTF8, FileBufferSize)
				{
					AutoFlush = false
				};
			}
			catch (Exception ex)
			{
				try
				{
					Console.ForegroundColor = ConsoleColor.Red;
					Console.WriteLine("Fatal startup exception. Could not write to \"{0}\". Exception details:\n{1}", logFilePath, ex);
				}
				finally
				{
					Console.ForegroundColor = ConsoleColor.Gray;
				}

				throw;
			}

			this.fileWriterThread = new Thread(ProcessFileQueue)
			{
				IsBackground = true,
				Name = "TSAPI-LogWriter"
			};
			this.fileWriterThread.Start();
		}

		public void Detach()
		{
			FlushPendingFileLines();
		}

		public void ServerWriteLine(string message, TraceLevel kind)
		{
			this.WriteLine("Server API", message, kind);
		}

		public void PluginWriteLine(TerrariaPlugin plugin, string message, TraceLevel kind)
		{
			this.WriteLine(plugin.Name, message, kind);
		}

		protected virtual void WriteLine(string context, string message, TraceLevel kind)
		{
			if (kind == TraceLevel.Off || Volatile.Read(ref this.disposed) != 0)
				return;

			if (kind != TraceLevel.Verbose)
			{
				WriteToConsole(context, message, kind);
			}

			this.pendingFileLines.Enqueue(string.Format("[{0:MM/dd/yy HH:mm:ss}] [{1}] {2}: {3}", DateTime.Now, context, kind, message));
			this.pendingSignal.Set();
		}

		private void WriteToConsole(string context, string message, TraceLevel kind)
		{
			lock (consoleWriteLock)
			{
				try
				{
					switch (kind)
					{
						case TraceLevel.Error:
							Console.ForegroundColor = ConsoleColor.Red;
							break;
						case TraceLevel.Warning:
							Console.ForegroundColor = ConsoleColor.Yellow;
							break;
						case TraceLevel.Info:
							Console.ForegroundColor = ConsoleColor.Gray;
							break;
					}

					Console.WriteLine("[{0}] {1} {2}", context, kind, message);
				}
				finally
				{
					Console.ForegroundColor = ConsoleColor.Gray;
				}
			}
		}

		private void ProcessFileQueue()
		{
			long lastFlushTimestamp = Stopwatch.GetTimestamp();

			while (true)
			{
				this.pendingSignal.WaitOne(50);

				int written = 0;
				while (written < MaxBatchSize && this.pendingFileLines.TryDequeue(out string line))
				{
					this.LogFileWriter.WriteLine(line);
					written++;
				}

				if (written > 0)
				{
					long nowTimestamp = Stopwatch.GetTimestamp();
					bool flushByTime = Stopwatch.GetElapsedTime(lastFlushTimestamp, nowTimestamp) >= FlushInterval;
					if (this.disposeRequested || written == MaxBatchSize || flushByTime || this.pendingFileLines.IsEmpty)
					{
						this.LogFileWriter.Flush();
						lastFlushTimestamp = nowTimestamp;
					}
				}

				if (this.disposeRequested && this.pendingFileLines.IsEmpty)
				{
					this.LogFileWriter.Flush();
					return;
				}
			}
		}

		private void FlushPendingFileLines()
		{
			while (this.pendingFileLines.TryDequeue(out string line))
			{
				this.LogFileWriter.WriteLine(line);
			}

			this.LogFileWriter.Flush();
		}

		~ServerLogWriter()
		{
			this.Dispose(false);
		}

		public void Dispose()
		{
			this.Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (Interlocked.Exchange(ref this.disposed, 1) != 0)
				return;

			this.disposeRequested = true;
			this.pendingSignal.Set();

			if (!disposing)
				return;

			if (this.fileWriterThread.IsAlive && Thread.CurrentThread != this.fileWriterThread)
			{
				this.fileWriterThread.Join(TimeSpan.FromSeconds(2));
			}

			FlushPendingFileLines();
			this.LogFileWriter.Dispose();
			this.pendingSignal.Dispose();
		}
	}
}
