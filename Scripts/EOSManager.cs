using Epic.OnlineServices;
using Epic.OnlineServices.Platform;
using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using FileAccess = Godot.FileAccess;

public partial class EOSManager : Node
{
	public static EOSManager Instance { get; private set; }

	private PlatformInterface _platformInterface;

	void LoadNativeEOS()
	{
		NativeLibrary.SetDllImportResolver(typeof(EOSManager).Assembly, (libraryName, assembly, searchPath) =>
		{
			if (libraryName.Contains("EOS"))
			{
				string fileName = string.Empty;

				if (OperatingSystem.IsWindows()) fileName = "EOSSDK-Win64-Shipping.dll";
				else if (OperatingSystem.IsLinux()) fileName = "libEOSSDK-Linux-Shipping.so";
				else if (OperatingSystem.IsAndroid())
				{
					// Android bundles native binaries inside the APK.
					// The Android OS dynamic linker locates them automatically by name.
					if (NativeLibrary.TryLoad("EOSSDK", out IntPtr androidHandle)) return androidHandle;
					if (NativeLibrary.TryLoad("libEOSSDK.so", out IntPtr androidHandleLong)) return androidHandleLong;

					GD.PrintErr("EOS Dynamic Linker failed to find Android native binary.");
					return IntPtr.Zero;
				}

				if (!string.IsNullOrEmpty(fileName))
				{
					// Path Option A: Check the root folder (if flattened via <Link> in csproj)
					string rootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
					if (File.Exists(rootPath) && NativeLibrary.TryLoad(rootPath, out IntPtr rootHandle))
						return rootHandle;

					// Path Option B: Check your explicit relative path structure
					string subFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EOS", "Bin", fileName);
					if (File.Exists(subFolderPath) && NativeLibrary.TryLoad(subFolderPath, out IntPtr subFolderHandle))
						return subFolderHandle;
				}
			}

			return IntPtr.Zero;
		});
	}
	public override void _Ready()
	{
		LoadNativeEOS();

		var env = new Dictionary<string, string>();

		if (!FileAccess.FileExists("res://.env"))
		{
			GD.PrintErr($"[EnvLoader] Warning: .env file not found!");
			return;
		}

		using var file = FileAccess.Open("res://.env", FileAccess.ModeFlags.Read);
		while (!file.EofReached())
		{
			string line = file.GetLine().Trim();

			if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
				continue;

			int index = line.IndexOf('=');
			if (index > 0)
			{
				string key = line.Substring(0, index).Trim();
				string value = line.Substring(index + 1).Trim();
				env[key] = value;
			}
		}

		if (!env.ContainsKey("EOS_PRODUCT_ID") || !env.ContainsKey("EOS_CLIENT_ID"))
		{
			GD.PrintErr("[EOS] Initialization aborted: Missing required .env keys.");
			return;
		}

		var initOptions = new InitializeOptions()
		{
			ProductName = "MyGodotGame",
			ProductVersion = "1.0.0"
		};

		var initResult = PlatformInterface.Initialize(ref initOptions);
		if (initResult != Result.Success)
		{
			GD.PrintErr($"[EOS] SDK Initialization failed: {initResult}");
			return;
		}

		var options = new Options()
		{
			ProductId = env["EOS_PRODUCT_ID"],
			SandboxId = env["EOS_SANDBOX_ID"],
			DeploymentId = env["EOS_DEPLOYMENT_ID"],
			ClientCredentials = new ClientCredentials()
			{
				ClientId = env["EOS_CLIENT_ID"],
				ClientSecret = env["EOS_CLIENT_SECRET"]
			},
			IsServer = false
		};

		_platformInterface = PlatformInterface.Create(ref options);
		if (_platformInterface == null)
		{
			GD.PrintErr("[EOS] Failed to create platform interface instance.");
			return;
		}

		GD.Print("[EOS] Successfully initialized using environment variables!");

		if (Instance != null)
			GD.PrintErr("[EOS] Warning: Multiple EOSManager instances detected. This may lead to unexpected behavior.");
		Instance = this;
	}

	public override void _Process(double delta)
	{
		_platformInterface?.Tick();
	}

	public override void _Notification(int what)
	{
		if (what == NotificationWMCloseRequest)
		{
			_platformInterface?.Release();
			_platformInterface = null;
			PlatformInterface.Shutdown();
			GD.Print("[EOS] Cleanly shut down.");
		}
	}
}
