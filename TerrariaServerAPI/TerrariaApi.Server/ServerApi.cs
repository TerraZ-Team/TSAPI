using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Linq;
using Terraria;
using TerrariaApi.Reporting;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TerrariaApi.Server
{
	// TODO: Maybe re-implement a reload functionality for plugins, but you'll have to load all assemblies into their own
	// AppDomain in order to unload them again later. Beware that having them in their own AppDomain might cause threading
	// problems as usual locks will only work in their own AppDomains.
	public static class ServerApi
	{
		public const string PluginsPath = "ServerPlugins";
		private const string PluginScanCacheFileName = "pluginscan.cache.json";

		/// <summary>
		/// Returns the first value from <see cref="AdditionalPluginsPaths"/> if it exists, otherwise null.
		/// </summary>
		[Obsolete("Use AdditionalPluginsPaths instead", error: false)]
		public static string? AdditionalPluginsPath => AdditionalPluginsPaths.FirstOrDefault();

		/// <summary> A list of all plugin paths specified by the -additionalplugins flag. </summary>
		public static ImmutableList<string> AdditionalPluginsPaths { get; private set; } = ImmutableList.Create<string>();
		public static readonly Version ApiVersion = new Version(2, 1, 0, 0);
		private static Main game;
		private static readonly Dictionary<string, Assembly> loadedAssemblies =
			new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
		private static readonly object loadedAssembliesLock = new object();
		private static readonly List<PluginContainer> plugins = new List<PluginContainer>();

		internal static readonly CrashReporter reporter = new CrashReporter();

		public static bool IgnoreVersion
		{
			get;
			set;
		}
		public static string ServerPluginsDirectoryPath
		{
			get;
			private set;
		}
		public static ReadOnlyCollection<PluginContainer> Plugins
		{
			get { return new ReadOnlyCollection<PluginContainer>(plugins); }
		}
		public static HookManager Hooks
		{
			get;
			private set;
		}
		public static LogWriterManager LogWriter
		{
			get;
			private set;
		}
		public static ProfilerManager Profiler
		{
			get;
			private set;
		}
		public static bool IsWorldRunning
		{
			get;
			internal set;
		}
		public static bool RunningMono { get; private set; }
		public static bool ForceUpdate { get; private set; }
		public static bool UseAsyncSocketsInMono { get; private set; }
		public static bool LoadPluginSymbols { get; private set; }
		internal static bool RuntimeProfileEnabled { get; private set; }
		internal static int RuntimeProfileIntervalSeconds { get; private set; }
		internal static int RuntimeProfileTop { get; private set; }
		static ServerApi()
		{
			AppContext.SetSwitch("Switch.System.Diagnostics.StackTrace.ShowILOffsets", true);
			Dictionary<string, string> args = Utils.ParseArguements(Environment.GetCommandLineArgs());
			Hooks = new HookManager();
			LogWriter = new LogWriterManager(enabled: !args.ContainsKey("-nolog"));
			Profiler = new ProfilerManager();

			UseAsyncSocketsInMono = false;
			ForceUpdate = false;
			LoadPluginSymbols = false;
			RuntimeProfileEnabled = false;
			RuntimeProfileIntervalSeconds = 60;
			RuntimeProfileTop = 10;
			Type t = Type.GetType("Mono.Runtime");
			RunningMono = (t != null);
			Main.SkipAssemblyLoad = true;
		}

		internal static void Initialize(string[] commandLineArgs, Main game)
		{
			Profiler.BeginMeasureServerInitTime();
			ServerApi.LogWriter.ServerWriteLine(
				string.Format("TerrariaApi - Server v{0} started.", ApiVersion), TraceLevel.Verbose);
			ServerApi.LogWriter.ServerWriteLine(
				"\tCommand line: " + Environment.CommandLine, TraceLevel.Verbose);
			ServerApi.LogWriter.ServerWriteLine(
				string.Format("\tOS: {0} (64bit: {1})", Environment.OSVersion, Environment.Is64BitOperatingSystem), TraceLevel.Verbose);
			ServerApi.LogWriter.ServerWriteLine(
				"\tMono: " + RunningMono, TraceLevel.Verbose);

			ServerApi.game = game;
			HandleCommandLine(commandLineArgs);
			ServerPluginsDirectoryPath = Path.Combine(AppContext.BaseDirectory, PluginsPath);

			if (!Directory.Exists(ServerPluginsDirectoryPath))
			{
				string lcDirectoryPath =
					Path.Combine(Path.GetDirectoryName(ServerPluginsDirectoryPath), PluginsPath.ToLower());

				if (Directory.Exists(lcDirectoryPath))
				{
					Directory.Move(lcDirectoryPath, ServerPluginsDirectoryPath);
					LogWriter.ServerWriteLine("Case sensitive filesystem detected, serverplugins directory has been renamed.", TraceLevel.Warning);
				}
				else
				{
					Directory.CreateDirectory(ServerPluginsDirectoryPath);
				}
			}

			// Add assembly resolver instructing it to use the server plugins directory as a search path.
			// TODO: Either adding the server plugins directory to PATH or as a privatePath node in the assembly config should do too.
			AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

			LoadPlugins();
			RuntimeMetrics.Configure(RuntimeProfileEnabled, RuntimeProfileIntervalSeconds, RuntimeProfileTop);
		}

		internal static void DeInitialize()
		{
			RuntimeMetrics.Shutdown();
			UnloadPlugins();
			Profiler.Deatch();
			LogWriter.Deatch();
		}

		internal static void HandleCommandLine(string[] parms)
		{
			Dictionary<string, string> args = Utils.ParseArguements(parms);

			bool isAutoCreating = false;

			foreach (KeyValuePair<string, string> arg in args)
			{
				// Note that the flag -nolog also exists in the constructor, but it can't be here because
				// the log writer initializes before this code is run
				switch (arg.Key.ToLower())
				{
					case "-ignoreversion":
						{
							ServerApi.IgnoreVersion = true;
							ServerApi.LogWriter.ServerWriteLine(
								"Plugin versions are no longer being regarded, you are on your own! If problems arise, TShock developers will not help you with issues regarding this.",
								TraceLevel.Warning);

							break;
						}
					case "-forceupdate":
						{
							ServerApi.ForceUpdate = true;
							ServerApi.LogWriter.ServerWriteLine(
								"Forcing game updates regardless of players! This is experimental, and will cause constant CPU usage, you are on your own.",
								TraceLevel.Warning);

							break;
						}
					case "-asyncmono":
						{
							ServerApi.UseAsyncSocketsInMono = true;
							ServerApi.LogWriter.ServerWriteLine(
								"Forcing Mono to use asynchronous sockets.  This is highly experimental and may not work on all versions of Mono.",
								TraceLevel.Warning);
							break;
						}
					case "-players":
					case "-maxplayers":
						{
							int playerCount;
							if (!Int32.TryParse(arg.Value, out playerCount))
							{
								ServerApi.LogWriter.ServerWriteLine("Invalid player count. Using 8", TraceLevel.Warning);

								playerCount = 8;
							}

							game.SetNetPlayers(playerCount);

							break;
						}
					case "-pass":
					case "-password":
						{
							Netplay.ServerPassword = arg.Value;

							break;
						}
					case "-worldname":
						{
							game.SetWorldName(arg.Value);

							break;
						}
					case "-world":
						{
							if (File.Exists(arg.Value))
							{
								game.SetWorld(arg.Value, false);
							}
							else
							{
								Main.autoGenFileLocation = arg.Value;
								Main.ActiveWorldFileData = new Terraria.IO.WorldFileData(arg.Value, false);
							}

							var full_path = Path.GetFullPath(arg.Value);
							Main.WorldPath = Path.GetDirectoryName(full_path);
							Main.worldName = Path.GetFileNameWithoutExtension(full_path);

							break;
						}
					case "-motd":
						{
							game.NewMOTD(arg.Value);

							break;
						}
					case "-banlist":
						{
							Netplay.BanFilePath = arg.Value;

							break;
						}
					case "-autoshutdown":
						{
							game.EnableAutoShutdown();

							break;
						}
					case "-secure":
						{
							Netplay.SpamCheck = true;

							break;
						}
					case "-autocreate":
						{
							game.autoCreate(arg.Value);
							isAutoCreating = true;

							break;
						}
					case "-difficulty":
						{
							if (!isAutoCreating)
							{
								LogWriter.ServerWriteLine("Ignoring difficulty command line flag because server is starting in interactive mode without autocreate", TraceLevel.Warning);
								continue;
							}

							// If the arg isn't an integer, or its an incorrect value, we want to ignore it
							if (int.TryParse(arg.Value, out int dif))
							{
								if (dif >= 0 && dif <= 3)
								{
									Main.GameMode = dif;
								}
							}
							else
							{
								LogWriter.ServerWriteLine("Unexpected difficulty value. Expected values are 0-3.", TraceLevel.Warning);
							}

							break;
						}
					case "-loadlib":
						{
							game.loadLib(arg.Value);

							break;
						}
					case "-crashdir":
						CrashReporter.crashReportPath = arg.Value;
						break;
					case "-additionalplugins":
						AdditionalPluginsPaths = arg.Value.Split(',').ToImmutableList();
						break;
					case "-loadsymbols":
						LoadPluginSymbols = true;
						LogWriter.ServerWriteLine(
							"Plugin symbol loading enabled. Startup can be slower due to .pdb reads.",
							TraceLevel.Warning);
						break;
					case "-runtimeprofile":
						RuntimeProfileEnabled = true;
						break;
					case "-runtimeprofileinterval":
						if (int.TryParse(arg.Value, out int runtimeProfileInterval))
						{
							RuntimeProfileIntervalSeconds = Math.Clamp(runtimeProfileInterval, 5, 3600);
						}
						else
						{
							LogWriter.ServerWriteLine(
								"Invalid runtime profile interval, expected integer seconds. Using default 60.",
								TraceLevel.Warning);
							RuntimeProfileIntervalSeconds = 60;
						}

						break;
					case "-runtimeprofiletop":
						if (int.TryParse(arg.Value, out int runtimeProfileTop))
						{
							RuntimeProfileTop = Math.Clamp(runtimeProfileTop, 1, 30);
						}
						else
						{
							LogWriter.ServerWriteLine(
								"Invalid runtime profile top value, expected integer. Using default 10.",
								TraceLevel.Warning);
							RuntimeProfileTop = 10;
						}

						break;
				}
			}
		}

		/// <summary>
		/// Tests to see if a plugin is using an incompatible architecture
		/// </summary>
		/// <param name="file">File info of the plugin</param>
		/// <param name="data">File contents</param>
		static void TryCheckArchitecture(FileInfo file, byte[] data)
		{
			using var ms = new MemoryStream(data);
			using var pe = new PEReader(ms);
			if (pe.HasMetadata)
			{
				var currentArch = RuntimeInformation.ProcessArchitecture;
				var laa = (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.LargeAddressAware) != 0;
				Architecture? asmArch = pe.PEHeaders.CoffHeader.Machine switch
				{
					Machine.IA64 => Architecture.X64,
					Machine.Arm64 => Architecture.Arm64,
					Machine.Amd64 => Architecture.X64,
					Machine.I386 => laa ? Architecture.X64 : Architecture.X86,
					Machine.Arm => Architecture.Arm,
					_ => null,
				};
				if (asmArch is not null && currentArch != asmArch)
					LogWriter.ServerWriteLine($"{file.Name} was built for {asmArch} but expected it to be compatible with {currentArch}.", TraceLevel.Error);
			}
		}

		internal static void LoadPlugins()
		{
			string ignoredPluginsFilePath = Path.Combine(ServerPluginsDirectoryPath, "ignoredplugins.txt");

			DangerousPluginDetector detector = new DangerousPluginDetector();

			HashSet<string> ignoredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (File.Exists(ignoredPluginsFilePath))
			{
				foreach (string raw in File.ReadLines(ignoredPluginsFilePath))
				{
					string fileName = raw.Trim();
					if (fileName.Length == 0 || fileName.StartsWith("#"))
						continue;

					ignoredFiles.Add(fileName);
				}
			}

			List<FileInfo> fileInfos = DeduplicatePluginFiles(EnumeratePluginFiles());
			Dictionary<string, (byte[] pe, byte[] symbols, Exception error)> preloadedPluginBinaries =
				PreloadPluginBinaries(fileInfos, ignoredFiles);
			Dictionary<string, PluginScanCacheEntry> cachedPluginScanEntries = LoadPluginScanCacheEntries();
			var updatedPluginScanEntries = new Dictionary<string, PluginScanCacheEntry>(StringComparer.OrdinalIgnoreCase);

			Dictionary<TerrariaPlugin, Stopwatch> pluginInitWatches = new Dictionary<TerrariaPlugin, Stopwatch>();
			foreach (FileInfo fileInfo in fileInfos)
			{
				string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileInfo.Name);
				if (ignoredFiles.Contains(fileNameWithoutExtension))
				{
					LogWriter.ServerWriteLine(
						string.Format("{0} was ignored from being loaded.", fileNameWithoutExtension), TraceLevel.Verbose);

					continue;
				}

				try
				{
					Assembly assembly;
					byte[] pe = null;
					try
					{
						if (preloadedPluginBinaries.TryGetValue(fileInfo.FullName, out var preloaded))
						{
							if (preloaded.error != null)
								ExceptionDispatchInfo.Capture(preloaded.error).Throw();

							pe = preloaded.pe;
							assembly = GetOrLoadAssembly(
								fileNameWithoutExtension,
								() => Assembly.Load(preloaded.pe, preloaded.symbols));
						}
						else
						{
							assembly = GetOrLoadAssembly(
								fileNameWithoutExtension,
								() => Assembly.Load(
									pe = File.ReadAllBytes(fileInfo.FullName),
									ReadPluginSymbols(fileInfo.FullName)));
						}
					}
					catch (BadImageFormatException)
					{
						continue;
					}
					catch (FileLoadException)
					{
						if (pe is null)
							pe = File.ReadAllBytes(fileInfo.FullName);

						TryCheckArchitecture(fileInfo, pe);
						throw;
					}

					pe ??= File.ReadAllBytes(fileInfo.FullName);

					if (!InvalidateAssembly(assembly, fileInfo.Name))
						continue;

					PluginScanCacheEntry scanEntry = GetCachedOrScannedPluginEntry(
						assembly,
						fileInfo,
						pe,
						cachedPluginScanEntries,
						updatedPluginScanEntries,
						out IReadOnlyList<Type> pluginTypes);

					bool isDangerous = scanEntry.IsDangerous ?? detector.MaliciousAssembly(assembly);
					scanEntry.IsDangerous = isDangerous;
					updatedPluginScanEntries[fileInfo.FullName] = scanEntry;

					if (isDangerous)
					{
						LogWriter.ServerWriteLine(string.Format("Assembly {0} {1} has been identified to the TShock Team as a dangerous plugin and needs to be removed.", assembly.GetName().Name, assembly.GetName().Version), TraceLevel.Error);
						LogWriter.ServerWriteLine(string.Format("Continuing to use {0} may damage your server, your data, or your computer. For your safety, this plugin has been disabled.", assembly.GetName().Name), TraceLevel.Error);
						continue;
					}

					foreach (Type type in pluginTypes)
					{
						object[] customAttributes = type.GetCustomAttributes(typeof(ApiVersionAttribute), false);
						if (customAttributes.Length == 0)
							continue;

						if (!IgnoreVersion)
						{
							var apiVersionAttribute = (ApiVersionAttribute)customAttributes[0];
							Version apiVersion = apiVersionAttribute.ApiVersion;
							if (apiVersion.Major != ApiVersion.Major || apiVersion.Minor != ApiVersion.Minor)
							{
								LogWriter.ServerWriteLine(
									string.Format("Plugin \"{0}\" is designed for a different Server API version ({1}) and was ignored.",
									type.FullName, apiVersion.ToString(2)), TraceLevel.Warning);

								continue;
							}
						}

						TerrariaPlugin pluginInstance;
						try
						{
							Stopwatch initTimeWatch = new Stopwatch();
							initTimeWatch.Start();

							pluginInstance = (TerrariaPlugin)Activator.CreateInstance(type, game);

							initTimeWatch.Stop();
							pluginInitWatches.Add(pluginInstance, initTimeWatch);
						}
						catch (Exception ex)
						{
							// Broken plugins better stop the entire server init.
							throw new InvalidOperationException(
								string.Format("Could not create an instance of plugin class \"{0}\".", type.FullName), ex);
						}
						plugins.Add(new PluginContainer(pluginInstance));
					}
				}
				catch (Exception ex)
				{
					// Broken assemblies / plugins better stop the entire server init.
					throw new InvalidOperationException(
						string.Format("Failed to load assembly \"{0}\".", fileInfo.Name), ex);
				}
			}

			SavePluginScanCacheEntries(updatedPluginScanEntries);

			IOrderedEnumerable<PluginContainer> orderedPluginSelector =
				from x in Plugins
				orderby x.Plugin.Order, x.Plugin.Name
				select x;

			foreach (PluginContainer current in orderedPluginSelector)
			{
				Stopwatch initTimeWatch = pluginInitWatches[current.Plugin];
				initTimeWatch.Start();

				try
				{
					current.Initialize();
				}
				catch (Exception ex)
				{
					// Broken plugins better stop the entire server init.
					throw new InvalidOperationException(string.Format(
						"Plugin \"{0}\" has thrown an exception during initialization.", current.Plugin.Name), ex);
				}

				initTimeWatch.Stop();
				LogWriter.ServerWriteLine(string.Format(
					"Plugin {0} v{1} (by {2}) initiated.", current.Plugin.Name, current.Plugin.Version, current.Plugin.Author),
					TraceLevel.Info);
			}

			if (Profiler.WrappedProfiler != null)
			{
				foreach (var pluginWatchPair in pluginInitWatches)
				{
					TerrariaPlugin plugin = pluginWatchPair.Key;
					Stopwatch initTimeWatch = pluginWatchPair.Value;

					Profiler.InputPluginInitTime(plugin, initTimeWatch.Elapsed);
				}
			}
		}

		internal static void UnloadPlugins()
		{
			var pluginUnloadWatches = new Dictionary<PluginContainer, Stopwatch>();
			foreach (PluginContainer pluginContainer in plugins)
			{
				Stopwatch unloadWatch = new Stopwatch();
				unloadWatch.Start();

				try
				{
					pluginContainer.DeInitialize();
				}
				catch (Exception ex)
				{
					LogWriter.ServerWriteLine(string.Format(
						"Plugin \"{0}\" has thrown an exception while being deinitialized:\n{1}", pluginContainer.Plugin.Name, ex),
						TraceLevel.Error);
				}

				unloadWatch.Stop();
				pluginUnloadWatches.Add(pluginContainer, unloadWatch);
			}

			foreach (PluginContainer pluginContainer in plugins)
			{
				Stopwatch unloadWatch = pluginUnloadWatches[pluginContainer];
				unloadWatch.Start();

				try
				{
					pluginContainer.Dispose();
				}
				catch (Exception ex)
				{
					LogWriter.ServerWriteLine(string.Format(
						"Plugin \"{0}\" has thrown an exception while being disposed:\n{1}", pluginContainer.Plugin.Name, ex),
						TraceLevel.Error);
				}

				unloadWatch.Stop();
				Profiler.InputPluginUnloadTime(pluginContainer.Plugin, unloadWatch.Elapsed);
			}
		}

		private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
		{
			string fileName = args.Name.Split(',')[0];
			string path = Path.Combine(ServerPluginsDirectoryPath, fileName + ".dll");
			try
			{
				if (!File.Exists(path))
					return null;

				Assembly assembly = GetOrLoadAssembly(
					fileName,
					() => Assembly.Load(File.ReadAllBytes(path), ReadPluginSymbols(path)));
				// We just do this to return a proper error message incase this is a resolved plugin assembly
				// referencing an old TerrariaServer version.
				if (!InvalidateAssembly(assembly, fileName))
					throw new InvalidOperationException(
						"The assembly is referencing a version of TerrariaServer prior 1.14.");

				return assembly;
			}
			catch (Exception ex)
			{
				LogWriter.ServerWriteLine(
					string.Format("Error on resolving assembly \"{0}.dll\":\n{1}", fileName, ex),
					TraceLevel.Error);
			}
			return null;
		}

		private static Assembly GetOrLoadAssembly(string assemblyName, Func<Assembly> factory)
		{
			lock (loadedAssembliesLock)
			{
				if (loadedAssemblies.TryGetValue(assemblyName, out Assembly loaded))
					return loaded;

				Assembly assembly = factory();
				loadedAssemblies[assemblyName] = assembly;
				return assembly;
			}
		}

		private static List<FileInfo> DeduplicatePluginFiles(IEnumerable<FileInfo> files)
		{
			var result = new List<FileInfo>();
			var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (FileInfo file in files)
			{
				if (!seenPaths.Add(file.FullName))
				{
					LogWriter.ServerWriteLine(
						$"Duplicate plugin file skipped: \"{file.FullName}\".",
						TraceLevel.Verbose);
					continue;
				}

				result.Add(file);
			}

			return result;
		}

		private static PluginScanCacheEntry GetCachedOrScannedPluginEntry(
			Assembly assembly,
			FileInfo fileInfo,
			byte[] pe,
			IReadOnlyDictionary<string, PluginScanCacheEntry> cachedEntries,
			IDictionary<string, PluginScanCacheEntry> updatedEntries,
			out IReadOnlyList<Type> pluginTypes)
		{
			long fileSize = pe.LongLength;
			long lastWriteUtcTicks = fileInfo.LastWriteTimeUtc.Ticks;
			string hash = ComputeSha256(pe);

			if (cachedEntries.TryGetValue(fileInfo.FullName, out PluginScanCacheEntry cachedEntry) &&
				cachedEntry.Size == fileSize &&
				cachedEntry.LastWriteUtcTicks == lastWriteUtcTicks &&
				string.Equals(cachedEntry.Sha256, hash, StringComparison.OrdinalIgnoreCase))
			{
				List<Type> cachedTypes = ResolveCachedPluginTypes(assembly, cachedEntry.PluginTypeNames);
				if (cachedTypes.Count == cachedEntry.PluginTypeNames.Count)
				{
					updatedEntries[fileInfo.FullName] = cachedEntry;
					pluginTypes = cachedTypes;
					return cachedEntry;
				}
			}

			List<Type> scannedTypes = ScanPluginTypes(assembly);
			PluginScanCacheEntry newEntry = new PluginScanCacheEntry
			{
				FullPath = fileInfo.FullName,
				Size = fileSize,
				LastWriteUtcTicks = lastWriteUtcTicks,
				Sha256 = hash,
				PluginTypeNames = scannedTypes
					.Select(static type => type.FullName)
					.Where(static typeName => !string.IsNullOrWhiteSpace(typeName))
					.Cast<string>()
					.ToList(),
			};
			updatedEntries[fileInfo.FullName] = newEntry;

			pluginTypes = scannedTypes;
			return newEntry;
		}

		private static List<Type> ResolveCachedPluginTypes(Assembly assembly, IReadOnlyCollection<string> typeNames)
		{
			var result = new List<Type>(typeNames.Count);
			foreach (string typeName in typeNames)
			{
				if (string.IsNullOrWhiteSpace(typeName))
					continue;

				Type type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
				if (type is null || !type.IsPublic || type.IsAbstract || !type.IsSubclassOf(typeof(TerrariaPlugin)))
					continue;

				result.Add(type);
			}

			return result;
		}

		private static List<Type> ScanPluginTypes(Assembly assembly)
		{
			var result = new List<Type>();
			foreach (Type type in assembly.GetExportedTypes())
			{
				if (!type.IsPublic || type.IsAbstract || !type.IsSubclassOf(typeof(TerrariaPlugin)))
					continue;

				result.Add(type);
			}

			return result;
		}

		private static string ComputeSha256(byte[] data)
		{
			return Convert.ToHexString(SHA256.HashData(data));
		}

		private static Dictionary<string, PluginScanCacheEntry> LoadPluginScanCacheEntries()
		{
			string cachePath = GetPluginScanCachePath();
			if (!File.Exists(cachePath))
				return new Dictionary<string, PluginScanCacheEntry>(StringComparer.OrdinalIgnoreCase);

			try
			{
				PluginScanCacheModel model = JsonSerializer.Deserialize<PluginScanCacheModel>(File.ReadAllText(cachePath));
				if (model is null || model.Version != 1 || model.Entries is null)
					return new Dictionary<string, PluginScanCacheEntry>(StringComparer.OrdinalIgnoreCase);

				var result = new Dictionary<string, PluginScanCacheEntry>(StringComparer.OrdinalIgnoreCase);
				foreach (PluginScanCacheEntry entry in model.Entries)
				{
					if (string.IsNullOrWhiteSpace(entry.FullPath))
						continue;

					entry.PluginTypeNames ??= new List<string>();
					result[entry.FullPath] = entry;
				}

				return result;
			}
			catch (Exception ex)
			{
				LogWriter.ServerWriteLine(
					$"Failed to read plugin scan cache: {ex.Message}",
					TraceLevel.Warning);
				return new Dictionary<string, PluginScanCacheEntry>(StringComparer.OrdinalIgnoreCase);
			}
		}

		private static void SavePluginScanCacheEntries(
			IReadOnlyDictionary<string, PluginScanCacheEntry> entries)
		{
			string cachePath = GetPluginScanCachePath();
			string temporaryPath = cachePath + ".tmp";
			try
			{
				var model = new PluginScanCacheModel
				{
					Version = 1,
					Entries = entries.Values
						.OrderBy(static entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
						.ToList(),
				};

				string payload = JsonSerializer.Serialize(model);
				File.WriteAllText(temporaryPath, payload);
				File.Move(temporaryPath, cachePath, overwrite: true);
			}
			catch (Exception ex)
			{
				LogWriter.ServerWriteLine(
					$"Failed to write plugin scan cache: {ex.Message}",
					TraceLevel.Warning);
				if (File.Exists(temporaryPath))
					File.Delete(temporaryPath);
			}
		}

		private static string GetPluginScanCachePath()
		{
			return Path.Combine(ServerPluginsDirectoryPath, PluginScanCacheFileName);
		}

		private static IEnumerable<FileInfo> EnumeratePluginFiles()
		{
			var pluginsDirectory = new DirectoryInfo(ServerPluginsDirectoryPath);
			foreach (FileInfo fileInfo in pluginsDirectory.EnumerateFiles("*.dll"))
				yield return fileInfo;
			foreach (FileInfo fileInfo in pluginsDirectory.EnumerateFiles("*.dll-plugin"))
				yield return fileInfo;

			foreach (string additionalPath in AdditionalPluginsPaths)
			{
				if (string.IsNullOrWhiteSpace(additionalPath))
					continue;

				string fullPath = Path.Combine(AppContext.BaseDirectory, additionalPath);
				if (!Directory.Exists(fullPath))
				{
					LogWriter.ServerWriteLine(
						$"Additional plugins path not found: \"{additionalPath}\".",
						TraceLevel.Warning);
					continue;
				}

				var additionalDirectory = new DirectoryInfo(fullPath);
				foreach (FileInfo fileInfo in additionalDirectory.EnumerateFiles("*.dll"))
					yield return fileInfo;
				foreach (FileInfo fileInfo in additionalDirectory.EnumerateFiles("*.dll-plugin"))
					yield return fileInfo;
			}
		}

		private static byte[] ReadPluginSymbols(string assemblyPath)
		{
			if (!LoadPluginSymbols)
				return null;

			string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
			return File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null;
		}

		private static Dictionary<string, (byte[] pe, byte[] symbols, Exception error)> PreloadPluginBinaries(
			IReadOnlyCollection<FileInfo> fileInfos,
			IReadOnlySet<string> ignoredFiles)
		{
			return PreloadPluginBinariesAsync(fileInfos, ignoredFiles).GetAwaiter().GetResult();
		}

		private static async Task<Dictionary<string, (byte[] pe, byte[] symbols, Exception error)>> PreloadPluginBinariesAsync(
			IReadOnlyCollection<FileInfo> fileInfos,
			IReadOnlySet<string> ignoredFiles)
		{
			const int maxConcurrency = 4;
			using SemaphoreSlim limiter = new SemaphoreSlim(maxConcurrency, maxConcurrency);

			var preloaded = new ConcurrentDictionary<string, (byte[] pe, byte[] symbols, Exception error)>(
				StringComparer.OrdinalIgnoreCase);
			var tasks = new List<Task>(fileInfos.Count);

			foreach (FileInfo fileInfo in fileInfos)
			{
				string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileInfo.Name);
				if (ignoredFiles.Contains(fileNameWithoutExtension))
					continue;

				tasks.Add(PreloadPluginBinaryAsync(fileInfo, limiter, preloaded));
			}

			await Task.WhenAll(tasks).ConfigureAwait(false);
			return new Dictionary<string, (byte[] pe, byte[] symbols, Exception error)>(
				preloaded,
				StringComparer.OrdinalIgnoreCase);
		}

		private static async Task PreloadPluginBinaryAsync(
			FileInfo fileInfo,
			SemaphoreSlim limiter,
			ConcurrentDictionary<string, (byte[] pe, byte[] symbols, Exception error)> preloaded)
		{
			await limiter.WaitAsync().ConfigureAwait(false);
			try
			{
				byte[] pe = await File.ReadAllBytesAsync(fileInfo.FullName).ConfigureAwait(false);
				byte[] symbols = await ReadPluginSymbolsAsync(fileInfo.FullName).ConfigureAwait(false);
				preloaded[fileInfo.FullName] = (pe, symbols, null);
			}
			catch (Exception ex)
			{
				preloaded[fileInfo.FullName] = (null, null, ex);
			}
			finally
			{
				limiter.Release();
			}
		}

		private static async Task<byte[]> ReadPluginSymbolsAsync(string assemblyPath)
		{
			if (!LoadPluginSymbols)
				return null;

			string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
			return File.Exists(pdbPath) ? await File.ReadAllBytesAsync(pdbPath).ConfigureAwait(false) : null;
		}

		private sealed class PluginScanCacheModel
		{
			public int Version { get; set; }
			public List<PluginScanCacheEntry> Entries { get; set; } = new List<PluginScanCacheEntry>();
		}

		private sealed class PluginScanCacheEntry
		{
			public string FullPath { get; set; } = string.Empty;
			public long Size { get; set; }
			public long LastWriteUtcTicks { get; set; }
			public string Sha256 { get; set; } = string.Empty;
			public List<string> PluginTypeNames { get; set; } = new List<string>();
			public bool? IsDangerous { get; set; }
		}

		// Many types have changed with 1.14 and thus we won't even be able to check the ApiVersionAttribute of
		// plugin classes of assemblies targeting a TerrariaServer prior 1.14 as they can not be loaded at all.
		// We work around this by checking the referenced assemblies, if we notice a reference to the old
		// TerrariaServer assembly, we expect the plugin assembly to be outdated.
		private static bool InvalidateAssembly(Assembly assembly, string fileName)
		{
			AssemblyName[] referencedAssemblies = assembly.GetReferencedAssemblies();
			AssemblyName terrariaServerReference = referencedAssemblies.FirstOrDefault(an => an.Name == "TerrariaServer");
			if (terrariaServerReference != null && terrariaServerReference.Version == new Version(0, 0, 0, 0))
			{
				LogWriter.ServerWriteLine(
					string.Format("Plugin assembly \"{0}\" was compiled for a Server API version prior 1.14 and was ignored.",
					fileName), TraceLevel.Warning);

				return false;
			}

			return true;
		}
	}
}
