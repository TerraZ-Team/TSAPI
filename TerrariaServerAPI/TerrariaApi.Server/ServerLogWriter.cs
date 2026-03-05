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
		private readonly struct ConsoleLogEntry
		{
			public ConsoleLogEntry(string line, ConsoleColor color)
			{
				Line = line;
				Color = color;
			}

			public string Line { get; }
			public ConsoleColor Color { get; }
		}

		private const int FileBufferSize = 64 * 1024;
		private const int MaxFileBatchSize = 256;
		private const int MaxConsoleBatchSize = 256;
		private const int MaxConsoleQueueSize = 4096;
		private const int MaxFileQueueSize = 32768;
		private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
		private static readonly object consoleWriteLock = new object();
		private readonly object fileWriteLock = new object();

		protected StreamWriter LogFileWriter { get; private set; }
		private readonly ConcurrentQueue<string> pendingFileLines = new ConcurrentQueue<string>();
		private readonly ConcurrentQueue<ConsoleLogEntry> pendingConsoleLines = new ConcurrentQueue<ConsoleLogEntry>();
		private readonly AutoResetEvent pendingSignal = new AutoResetEvent(false);
		private readonly Thread logWorkerThread;
		private volatile bool disposeRequested;
		private int disposed;
		private int pendingConsoleCount;
		private int pendingFileCount;
		private int droppedFileLines;

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

			this.logWorkerThread = new Thread(ProcessQueues)
			{
				IsBackground = true,
				Name = "TSAPI-LogWriter"
			};
			this.logWorkerThread.Start();
		}

		public void Detach()
		{
			FlushPending();
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
				if (kind == TraceLevel.Error)
				{
					WriteToConsole(CreateConsoleEntry(context, message, kind));
				}
				else
				{
					EnqueueConsoleLine(context, message, kind);
				}
			}

			EnqueueFileLine(
				string.Format("[{0:MM/dd/yy HH:mm:ss}] [{1}] {2}: {3}", DateTime.Now, context, kind, message),
				kind);
		}

		private void EnqueueConsoleLine(string context, string message, TraceLevel kind)
		{
			ConsoleLogEntry entry = CreateConsoleEntry(context, message, kind);
			int queueSize = Interlocked.Increment(ref this.pendingConsoleCount);
			if (queueSize <= MaxConsoleQueueSize)
			{
				this.pendingConsoleLines.Enqueue(entry);
				this.pendingSignal.Set();
				return;
			}

			Interlocked.Decrement(ref this.pendingConsoleCount);
			if (kind == TraceLevel.Verbose)
				return;

			WriteToConsole(entry);
		}

		private static ConsoleLogEntry CreateConsoleEntry(string context, string message, TraceLevel kind)
		{
			ConsoleColor color = kind switch
			{
				TraceLevel.Error => ConsoleColor.Red,
				TraceLevel.Warning => ConsoleColor.Yellow,
				_ => ConsoleColor.Gray,
			};

			return new ConsoleLogEntry(string.Format("[{0}] {1} {2}", context, kind, message), color);
		}

		private void EnqueueFileLine(string line, TraceLevel kind)
		{
			int queueSize = Interlocked.Increment(ref this.pendingFileCount);
			if (queueSize <= MaxFileQueueSize)
			{
				this.pendingFileLines.Enqueue(line);
				this.pendingSignal.Set();
				return;
			}

			Interlocked.Decrement(ref this.pendingFileCount);
			Interlocked.Increment(ref this.droppedFileLines);

			if (kind == TraceLevel.Error || kind == TraceLevel.Warning)
			{
				lock (this.fileWriteLock)
				{
					this.LogFileWriter.WriteLine(line);
					this.LogFileWriter.Flush();
				}
			}
		}

		private static void WriteToConsole(ConsoleLogEntry entry)
		{
			lock (consoleWriteLock)
			{
				try
				{
					Console.ForegroundColor = entry.Color;
					Console.WriteLine(entry.Line);
				}
				finally
				{
					Console.ForegroundColor = ConsoleColor.Gray;
				}
			}
		}

		private void ProcessQueues()
		{
			long lastFlushTimestamp = Stopwatch.GetTimestamp();

			while (true)
			{
				this.pendingSignal.WaitOne(50);

				int fileWritten = 0;
				lock (this.fileWriteLock)
				{
					while (fileWritten < MaxFileBatchSize && this.pendingFileLines.TryDequeue(out string line))
					{
						Interlocked.Decrement(ref this.pendingFileCount);
						this.LogFileWriter.WriteLine(line);
						fileWritten++;
					}
				}

				int consoleWritten = 0;
				while (consoleWritten < MaxConsoleBatchSize && this.pendingConsoleLines.TryDequeue(out ConsoleLogEntry entry))
				{
					Interlocked.Decrement(ref this.pendingConsoleCount);
					WriteToConsole(entry);
					consoleWritten++;
				}

				if (fileWritten > 0)
				{
					long nowTimestamp = Stopwatch.GetTimestamp();
					bool flushByTime = Stopwatch.GetElapsedTime(lastFlushTimestamp, nowTimestamp) >= FlushInterval;
					if (this.disposeRequested || fileWritten == MaxFileBatchSize || flushByTime || this.pendingFileLines.IsEmpty)
					{
						lock (this.fileWriteLock)
						{
							this.LogFileWriter.Flush();
						}
						lastFlushTimestamp = nowTimestamp;
					}
				}

				int droppedFileLines = Interlocked.Exchange(ref this.droppedFileLines, 0);
				if (droppedFileLines > 0)
				{
					string droppedLine = string.Format(
						"[{0:MM/dd/yy HH:mm:ss}] [Server API] Warning: File log queue overflow. Dropped lines: {1}",
						DateTime.Now,
						droppedFileLines);
					lock (this.fileWriteLock)
					{
						this.LogFileWriter.WriteLine(droppedLine);
						this.LogFileWriter.Flush();
					}
				}

				if (this.disposeRequested && this.pendingFileLines.IsEmpty && Volatile.Read(ref this.pendingConsoleCount) == 0)
				{
					lock (this.fileWriteLock)
					{
						this.LogFileWriter.Flush();
					}
					return;
				}
			}
		}

		private void FlushPending()
		{
			while (this.pendingConsoleLines.TryDequeue(out ConsoleLogEntry entry))
			{
				Interlocked.Decrement(ref this.pendingConsoleCount);
				WriteToConsole(entry);
			}

			lock (this.fileWriteLock)
			{
				while (this.pendingFileLines.TryDequeue(out string line))
				{
					Interlocked.Decrement(ref this.pendingFileCount);
					this.LogFileWriter.WriteLine(line);
				}

				this.LogFileWriter.Flush();
			}
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

			if (this.logWorkerThread.IsAlive && Thread.CurrentThread != this.logWorkerThread)
			{
				this.logWorkerThread.Join(TimeSpan.FromSeconds(2));
			}

			FlushPending();
			this.LogFileWriter.Dispose();
			this.pendingSignal.Dispose();
		}
	}
}
