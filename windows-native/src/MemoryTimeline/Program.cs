using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace MemoryTimeline;

/// <summary>
/// Program entry point for unpackaged WinUI 3 application.
/// Handles Windows App SDK bootstrapper initialization.
/// </summary>
public static class Program
{
    /// <summary>
    /// The Jump List / command-line action token this process was launched with
    /// (e.g. "action:new-event"), or null when no action argument was supplied.
    /// Set once during startup, before the App is constructed; App.OnLaunched
    /// reads it to decide the initial navigation target.
    /// </summary>
    public static string? LaunchAction { get; private set; }

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MemoryTimeline",
        "startup.log"
    );

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // .NET 10 changed Microsoft.Data.Sqlite's timezone handling: reading a
        // stored timestamp that carries no offset now assumes UTC rather than
        // local time, and one that carries an offset comes back as UTC. Every
        // date in an existing memory-timeline.db was written under the old
        // rules, so adopting the new ones silently shifts historical events by
        // the local UTC offset. Keep the pre-10 behaviour until the stored
        // values are audited and, if needed, migrated.
        AppContext.SetSwitch("Microsoft.Data.Sqlite.Pre10TimeZoneHandling", true);

        Log("=== Application Starting ===");
        Log($"Args: {string.Join(", ", args)}");
        Log($"Current directory: {Environment.CurrentDirectory}");
        Log($"Process path: {Environment.ProcessPath}");

        CaptureLaunchAction(args);

        try
        {
            Log("Initializing COM...");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Log("COM initialized");

            Log("Checking if main instance...");
            bool isRedirect = DecideRedirection();

            if (!isRedirect)
            {
                Log("Starting as main instance...");
                Microsoft.UI.Xaml.Application.Start((p) =>
                {
                    Log("Inside Application.Start callback");
                    var context = new DispatcherQueueSynchronizationContext(
                        DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    Log("Creating App instance...");
                    new App();
                    Log("App instance created");
                });
                Log("Application.Start completed");
            }
            else
            {
                Log("Redirecting to existing instance");
            }

            Log("Main returning 0");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"FATAL ERROR: {ex.GetType().Name}: {ex.Message}");
            Log($"Stack trace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Log($"Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                Log($"Inner stack trace: {ex.InnerException.StackTrace}");
            }
            return 1;
        }
    }

    /// <summary>
    /// True for the activation tokens this app understands: Jump List quick
    /// actions ("action:...") and event deep links ("event:{id}", emitted by
    /// JumpListService recent-event entries and toast deep links).
    /// </summary>
    private static bool IsActivationToken(string arg)
    {
        return arg.StartsWith("action:", StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith("event:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scans startup arguments for an activation token ("action:..." or
    /// "event:{id}") and stores it in <see cref="LaunchAction"/>. Safe to call
    /// multiple times; the first token found wins.
    /// </summary>
    private static void CaptureLaunchAction(IEnumerable<string>? args)
    {
        if (LaunchAction != null || args == null)
        {
            return;
        }

        foreach (var arg in args)
        {
            if (!string.IsNullOrWhiteSpace(arg) && IsActivationToken(arg))
            {
                LaunchAction = arg;
                Log($"Captured launch action: {arg}");
                return;
            }
        }
    }

    /// <summary>
    /// Extracts an "action:" or "event:" token from an activation's payload,
    /// or returns null when the activation carries none. Used for both the
    /// cold-start activation (DecideRedirection) and redirected activations
    /// received while running (OnRedirectedActivation), so both paths parse
    /// tokens identically.
    /// </summary>
    internal static string? ExtractActionToken(AppActivationArguments args)
    {
        try
        {
            if (args.Kind == ExtendedActivationKind.Launch &&
                args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchData &&
                !string.IsNullOrWhiteSpace(launchData.Arguments))
            {
                foreach (var arg in launchData.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (IsActivationToken(arg))
                    {
                        return arg;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"ExtractActionToken error (non-fatal): {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Handles activations redirected from secondary instances. Fires on a
    /// background (non-UI) thread; App marshals to the UI thread, brings the
    /// main window to the foreground, and routes any "action:" token through
    /// the same navigation path App.OnLaunched uses.
    /// </summary>
    private static void OnRedirectedActivation(object? sender, AppActivationArguments args)
    {
        try
        {
            Log($"Redirected activation received. Kind: {args.Kind}");
            var actionToken = ExtractActionToken(args);
            Log($"Redirected activation action token: {actionToken ?? "(none)"}");
            App.OnRedirectedActivation(actionToken);
        }
        catch (Exception ex)
        {
            Log($"Redirected activation handling error (non-fatal): {ex.Message}");
        }
    }

    private static bool DecideRedirection()
    {
        try
        {
            Log("Getting current activation args...");
            bool isRedirect = false;
            AppActivationArguments args = AppInstance.GetCurrent().GetActivatedEventArgs();
            Log($"Activation kind: {args.Kind}");

            // Jump List activations arrive as Launch activations; their arguments
            // travel in the activation payload. Capture any "action:" token so
            // App.OnLaunched can navigate accordingly (no-op if already captured
            // from the raw command line in Main).
            var activationAction = ExtractActionToken(args);
            if (activationAction != null)
            {
                CaptureLaunchAction(new[] { activationAction });
            }

            ExtendedActivationKind kind = args.Kind;
            AppInstance keyInstance = AppInstance.FindOrRegisterForKey("MemoryTimelineMainInstance");
            Log($"Key instance found, IsCurrent: {keyInstance.IsCurrent}");

            if (keyInstance.IsCurrent)
            {
                Log("This is the main instance");

                // Secondary launches redirect their activation to this process via
                // RedirectActivationToAsync. Without this subscription those
                // redirected activations (second double-click, Jump List quick
                // action while the app is running) would be silently dropped.
                keyInstance.Activated += OnRedirectedActivation;
            }
            else
            {
                Log("Redirecting activation to main instance");
                isRedirect = true;
                RedirectActivationTo(args, keyInstance);
            }

            return isRedirect;
        }
        catch (Exception ex)
        {
            Log($"DecideRedirection error (non-fatal): {ex.Message}");
            // If redirection fails, just continue as main instance
            return false;
        }
    }

    #region Single-instance redirection (Windows App SDK AppLifecycle pattern)

    // Main runs on the STA thread. RedirectActivationToAsync completes only once
    // that thread has pumped the cross-process COM calls behind the redirect, so
    // blocking it with Task.Wait() can deadlock the secondary instance (the
    // Windows App SDK AppLifecycle guidance). The documented shape: run the
    // redirect on a worker thread and wait on a Win32 event with
    // CoWaitForMultipleObjects, which keeps pumping messages while it waits.

    private const uint CWMO_DEFAULT = 0;
    private const uint RedirectTimeoutMilliseconds = 15_000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint dwFlags, uint dwTimeout, uint cHandles, IntPtr[] pHandles, out uint lpdwIndex);

    /// <summary>
    /// Hands this (secondary) instance's activation to the registered main
    /// instance without blocking the STA thread's message pump. Bounded by
    /// <see cref="RedirectTimeoutMilliseconds"/> so a wedged main instance
    /// cannot leave an invisible second process alive forever; a timeout only
    /// costs that one activation (logged), the secondary still exits.
    /// </summary>
    private static void RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance)
    {
        var redirectEventHandle = CreateEvent(IntPtr.Zero, bManualReset: true, bInitialState: false, lpName: null);
        if (redirectEventHandle == IntPtr.Zero)
        {
            // Practically unreachable; keep the old behaviour but bounded so a
            // deadlock cannot hang the process.
            Log($"CreateEvent failed ({Marshal.GetLastWin32Error()}); falling back to a bounded synchronous wait");
            if (!keyInstance.RedirectActivationToAsync(args).AsTask().Wait(TimeSpan.FromMilliseconds(RedirectTimeoutMilliseconds)))
            {
                Log("Activation redirect timed out (fallback wait)");
            }
            return;
        }

        try
        {
            Task.Run(() =>
            {
                try
                {
                    keyInstance.RedirectActivationToAsync(args).AsTask().Wait();
                    Log("Activation redirected to the main instance");
                }
                catch (Exception ex)
                {
                    Log($"Activation redirect failed (non-fatal): {ex.Message}");
                }
                finally
                {
                    SetEvent(redirectEventHandle);
                }
            });

            // Pumps messages while waiting, which is what lets the redirect complete.
            var hr = CoWaitForMultipleObjects(
                CWMO_DEFAULT,
                RedirectTimeoutMilliseconds,
                1,
                new[] { redirectEventHandle },
                out _);
            if (hr != 0)
            {
                Log($"CoWaitForMultipleObjects returned 0x{hr:X8} (timeout or error); exiting the secondary instance anyway");
            }
        }
        finally
        {
            CloseHandle(redirectEventHandle);
        }
    }

    #endregion
}
