using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using WpfVisualTreeMcp.Injector;

namespace WpfVisualTreeMcp.Server.Services;

/// <summary>
/// Implementation of process management for WPF applications.
/// </summary>
public class ProcessManager : IProcessManager
{
    private readonly ILogger<ProcessManager> _logger;
    private readonly ProcessInjector _injector;
    private InspectionSession? _currentSession;
    private readonly object _lock = new();

    public ProcessManager(ILogger<ProcessManager> logger)
    {
        _logger = logger;
        _injector = new ProcessInjector();
    }

    public InspectionSession? CurrentSession
    {
        get
        {
            lock (_lock)
            {
                return _currentSession;
            }
        }
    }

    public Task<IReadOnlyList<WpfProcessInfo>> GetWpfProcessesAsync()
    {
        var wpfProcesses = new List<WpfProcessInfo>();

        try
        {
            var processes = Process.GetProcesses();

            foreach (var process in processes)
            {
                try
                {
                    // Check if this might be a WPF application
                    // A more robust check would involve querying loaded modules
                    if (IsLikelyWpfProcess(process))
                    {
                        var isAttached = _currentSession?.ProcessId == process.Id;

                        wpfProcesses.Add(new WpfProcessInfo
                        {
                            ProcessId = process.Id,
                            ProcessName = process.ProcessName,
                            MainWindowTitle = GetMainWindowTitle(process),
                            IsAttached = isAttached,
                            DotNetVersion = GetDotNetVersion(process),
                            RuntimeType = GetRuntimeType(process)
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not inspect process {ProcessId}", process.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enumerating processes");
        }

        return Task.FromResult<IReadOnlyList<WpfProcessInfo>>(wpfProcesses);
    }

    public async Task<InspectionSession> AttachToProcessAsync(int? processId, string? processName, bool autoInject = false)
    {
        Process? targetProcess = null;

        if (processId.HasValue)
        {
            try
            {
                targetProcess = Process.GetProcessById(processId.Value);
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException($"Process with ID {processId} not found");
            }
        }
        else if (!string.IsNullOrEmpty(processName))
        {
            var processes = Process.GetProcessesByName(processName.Replace(".exe", ""));
            targetProcess = processes.FirstOrDefault();

            if (targetProcess == null)
            {
                throw new InvalidOperationException($"Process with name '{processName}' not found");
            }
        }
        else
        {
            throw new ArgumentException("Either processId or processName must be provided");
        }

        // Verify it's a WPF process
        if (!IsLikelyWpfProcess(targetProcess))
        {
            _logger.LogWarning("Process {ProcessId} may not be a WPF application", targetProcess.Id);
        }

        // Create a new session
        var session = new InspectionSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            ProcessId = targetProcess.Id,
            MainWindowHandle = $"window_0x{targetProcess.MainWindowHandle:X}",
            AttachedAt = DateTime.UtcNow
        };

        lock (_lock)
        {
            _currentSession = session;
        }

        _logger.LogInformation("Attached to process {ProcessId} ({ProcessName})",
            targetProcess.Id, targetProcess.ProcessName);

        // Probe the inspector's named pipe first. It covers BOTH self-hosted mode (the
        // target app hosts the inspector itself) and re-attach to a previously injected
        // process. Module enumeration cannot detect the managed Inspector: assemblies
        // loaded through CLR hosting never appear in process.Modules.
        var inspectorActive = await IsInspectorActiveAsync(targetProcess.Id);
        if (inspectorActive)
        {
            _logger.LogInformation("Inspector pipe already active in process {ProcessId}", targetProcess.Id);
            session.InspectorStatus = "Loaded (active)";
        }
        else if (autoInject)
        {
            // Attempt to inject the Inspector DLL
            _logger.LogInformation("Inspector not active, attempting injection...");
            try
            {
                var inspectorPath = _injector.GetInspectorDllPath();
                var result = _injector.InjectIntoProcess(targetProcess.Id, inspectorPath);

                if (result)
                {
                    // Wait for the Inspector to initialize and create its named pipe.
                    // First-time CLR hosting inside a large process (e.g. a native shell
                    // host like NX) can take well over 10s, so wait up to 30s.
                    var pipeConnected = await WaitForInspectorPipeAsync(targetProcess.Id, TimeSpan.FromSeconds(30));
                    if (pipeConnected)
                    {
                        _logger.LogInformation("Inspector successfully injected and initialized");
                        session.InspectorStatus = "Loaded (injected)";
                    }
                    else
                    {
                        _logger.LogWarning("Inspector injected but named pipe not available after 30s");
                        session.InspectorStatus = "Injected - pipe timeout. Inspector may still be initializing; " +
                            "retry attach in a moment, or check the target process's " +
                            "%TEMP%\\WpfInspectorBootstrapper.log and %TEMP%\\WpfInspector_Debug_{pid}.log";
                    }
                }
                else
                {
                    _logger.LogWarning("Injection returned false - Inspector may not have loaded");
                    session.InspectorStatus = "Injection failed";
                }
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogError(ex, "Injection failed - required DLL not found: {FileName}", ex.FileName);
                session.InspectorStatus = $"Injection failed - {ex.FileName} not found";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Injection failed");
                session.InspectorStatus = $"Injection failed: {ex.Message}";
            }
        }
        else
        {
            _logger.LogWarning(
                "Inspector DLL not loaded in target process. " +
                "Use autoInject=true to inject automatically, or use self-hosted mode.");
            session.InspectorStatus = "Not loaded - use autoInject or self-hosted mode";
        }

        return session;
    }

    /// <summary>
    /// Waits for the Inspector's named pipe to become available.
    /// </summary>
    private async Task<bool> WaitForInspectorPipeAsync(int processId, TimeSpan timeout)
    {
        var pipeName = $"wpf_inspector_{processId}";
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < timeout)
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipeClient.ConnectAsync(500); // 500ms connection attempt
                return true;
            }
            catch (TimeoutException)
            {
                // Pipe not ready yet, wait and retry
                await Task.Delay(200);
            }
            catch (IOException)
            {
                // Pipe doesn't exist yet, wait and retry
                await Task.Delay(200);
            }
        }

        return false;
    }

    /// <summary>
    /// Probes whether an Inspector is already running in the target process, by
    /// attempting a short connection to its named pipe
    /// (<c>wpf_inspector_{processId}</c>).
    /// </summary>
    private static async Task<bool> IsInspectorActiveAsync(int processId)
    {
        var pipeName = $"wpf_inspector_{processId}";
        try
        {
            using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipeClient.ConnectAsync(500);
            return true;
        }
        catch (TimeoutException)
        {
            // Pipe doesn't exist (yet)
            return false;
        }
        catch (IOException)
        {
            // Pipe doesn't exist (yet)
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Pipe exists but was created by a different user / elevation level.
            // Treat as active: an inspector is there, we just can't reach it from here.
            return true;
        }
    }

    public Task DetachAsync(string sessionId)
    {
        lock (_lock)
        {
            if (_currentSession?.SessionId == sessionId)
            {
                _logger.LogInformation("Detaching from process {ProcessId}", _currentSession.ProcessId);
                _currentSession = null;
            }
        }

        return Task.CompletedTask;
    }

    private bool IsLikelyWpfProcess(Process process)
    {
        try
        {
            // Check if the process has a main window (most WPF apps do)
            if (process.MainWindowHandle == IntPtr.Zero)
                return false;

            // Check loaded modules for WPF assemblies
            // This requires appropriate permissions
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    var moduleName = module.ModuleName.ToLowerInvariant();
                    if (moduleName.Contains("presentationframework") ||
                        moduleName.Contains("presentationcore") ||
                        moduleName.Contains("wpfgfx"))
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // Can't access modules, fall back to heuristics
                // For now, just assume any process with a main window could be WPF
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private string? GetMainWindowTitle(Process process)
    {
        try
        {
            return string.IsNullOrEmpty(process.MainWindowTitle) ? null : process.MainWindowTitle;
        }
        catch
        {
            return null;
        }
    }

    private string? GetRuntimeType(Process process)
    {
        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                var name = module.ModuleName.ToLowerInvariant();
                if (name == "coreclr.dll") return "CoreCLR";
                if (name == "clr.dll" || name == "mscorwks.dll") return "Framework";
            }
        }
        catch { }
        return null;
    }

    private string? GetDotNetVersion(Process process)
    {
        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                var moduleName = module.ModuleName.ToLowerInvariant();
                if (moduleName == "clr.dll" || moduleName == "coreclr.dll" || moduleName == "mscorwks.dll")
                {
                    var version = module.FileVersionInfo.FileVersion;
                    return version;
                }
            }
        }
        catch
        {
            // Can't access modules
        }

        return null;
    }
}
