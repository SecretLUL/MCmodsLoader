using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCperfBooster.Core.Constants;
using MCperfBooster.Core.Models;
using MCperfBooster.Core.Services;

namespace MCperfBooster.Tests;

/// <summary>
/// Reports synchronously. Progress&lt;T&gt; posts to the thread pool, so its last report can
/// land after the awaited call has already returned.
/// </summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public SyncProgress(Action<T> handler) => _handler = handler;

    public void Report(T value) => _handler(value);
}

public class SelfUpdateTests : IDisposable
{
    // cmd.exe reads batch files in the OEM code page and expands '!' and '%', which is
    // what broke the old batch-script updater in folders like this one.
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"Down!oads 100% Jürgen_{Guid.NewGuid():N}");

    public SelfUpdateTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private string TargetExe => Path.Combine(_tempDir, "MCperfBooster.exe");
    private string BackupExe => Path.Combine(_tempDir, "MCperfBooster.old.exe");
    private string StagedExe => Path.Combine(_tempDir, "MCperfBooster.new.exe");

    private static byte[] FakePe(string marker) => Encoding.ASCII.GetBytes("MZ" + marker);

    private static AppUpdateInfo UpdateFor(byte[]? payload, string? sha256Override = null) => new()
    {
        CurrentVersion = "1.0.0",
        LatestVersion = "2.0.0",
        HasUpdate = true,
        DownloadUrl = "https://dummy.url/MCperfBooster.exe",
        DownloadSha256 = sha256Override ?? (payload == null ? null : Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant())
    };

    private static UpdateService ServiceServing(byte[] payload) => new(new HttpClient(new MockBinaryHttpHandler(payload)));

    [Fact]
    public async Task ApplyUpdate_InDirectoryWithUmlautBangAndPercent_SwapsAndCleansUp()
    {
        File.WriteAllBytes(TargetExe, FakePe("old"));
        byte[] payload = FakePe("new build");

        bool ok = await ServiceServing(payload).ApplyUpdateAsync(UpdateFor(payload), TargetExe, restartAndExit: false, null, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(payload, File.ReadAllBytes(TargetExe));
        Assert.Equal(FakePe("old"), File.ReadAllBytes(BackupExe));
        Assert.False(File.Exists(StagedExe));

        await UpdateService.CleanupAfterUpdateAsync(TargetExe, TimeSpan.FromSeconds(5));

        Assert.False(File.Exists(BackupExe));
        Assert.Equal(new[] { TargetExe }, Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task ApplyUpdate_WhileTargetExecutableIsRunning_SwapsAndDeletesBackupOnceItExits()
    {
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), TargetExe);
        long originalLength = new FileInfo(TargetExe).Length;

        var psi = new ProcessStartInfo(TargetExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("15");
        psi.ArgumentList.Add("127.0.0.1");

        using var running = Process.Start(psi)!;
        try
        {
            Thread.Sleep(300);
            Assert.False(running.HasExited, "The copied executable must be running for this test to mean anything");

            byte[] payload = FakePe("replacement");
            bool ok = await ServiceServing(payload).ApplyUpdateAsync(UpdateFor(payload), TargetExe, restartAndExit: false, null, CancellationToken.None);

            Assert.True(ok);
            Assert.Equal(payload, File.ReadAllBytes(TargetExe));
            Assert.Equal(originalLength, new FileInfo(BackupExe).Length);
            Assert.False(running.HasExited);

            // The running image cannot be deleted; cleanup keeps retrying until it exits.
            Task cleanup = UpdateService.CleanupAfterUpdateAsync(TargetExe, TimeSpan.FromSeconds(15));
            await Task.Delay(1000);
            Assert.True(File.Exists(BackupExe));

            running.Kill();
            running.WaitForExit(5000);
            await cleanup;

            Assert.False(File.Exists(BackupExe));
        }
        finally
        {
            try { if (!running.HasExited) running.Kill(); } catch { }
        }
    }

    [Fact]
    public async Task SwapExecutable_WhenFinalMoveFails_RollsBackToTheOriginal()
    {
        File.WriteAllBytes(TargetExe, FakePe("old"));
        File.WriteAllBytes(StagedExe, FakePe("new"));

        string? backup;
        // Held open without delete sharing, the staged file cannot be moved.
        using (new FileStream(StagedExe, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            backup = await UpdateService.SwapExecutableAsync(TargetExe, StagedExe);
        }

        Assert.Null(backup);
        Assert.Equal(FakePe("old"), File.ReadAllBytes(TargetExe));
        Assert.False(File.Exists(BackupExe));
    }

    [Fact]
    public async Task SwapExecutable_WhenStagedFileIsBrieflyLocked_RetriesAndSucceeds()
    {
        File.WriteAllBytes(TargetExe, FakePe("old"));
        File.WriteAllBytes(StagedExe, FakePe("new"));

        // A scanner holding the fresh file for half a second must not abort the update.
        var scanner = new FileStream(StagedExe, FileMode.Open, FileAccess.Read, FileShare.Read);
        _ = Task.Delay(500).ContinueWith(_ => scanner.Dispose());

        string? backup = await UpdateService.SwapExecutableAsync(TargetExe, StagedExe);

        Assert.Equal(BackupExe, backup);
        Assert.Equal(FakePe("new"), File.ReadAllBytes(TargetExe));
        Assert.False(File.Exists(StagedExe));
    }

    [Fact]
    public async Task ApplyUpdate_WhenNewBuildReportsIn_ExitsWithoutWaitingForTheTimeout()
    {
        File.WriteAllBytes(TargetExe, FakePe("old"));
        byte[] payload = FakePe("new");
        var service = ServiceServing(payload);
        service.StartupConfirmationTimeout = TimeSpan.FromSeconds(30);
        string? started = null, startArgs = null;
        bool exited = false;
        service.StartExecutable = (path, args) =>
        {
            started = path;
            startArgs = args;
            // What the new build's CleanupAfterUpdate does with its command line.
            UpdateService.SignalUpdateStarted(new[] { "--some-unknown-flag", args });
            return null;
        };
        service.ExitProcess = () => exited = true;

        var sw = Stopwatch.StartNew();
        bool ok = await service.ApplyUpdateAsync(UpdateFor(payload), TargetExe, restartAndExit: true, null, CancellationToken.None);

        Assert.True(ok);
        Assert.True(exited);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Took {sw.Elapsed}");
        Assert.Equal(TargetExe, started);
        Assert.Equal($"--updated-from={Environment.ProcessId}", startArgs);
        Assert.Equal(payload, File.ReadAllBytes(TargetExe));
    }

    [Fact]
    public async Task ApplyUpdate_WhenNewBuildExitsBeforeReportingIn_RestoresThePreviousBuild()
    {
        File.WriteAllBytes(TargetExe, FakePe("old"));
        byte[] payload = FakePe("new");
        var service = ServiceServing(payload);
        // Stands in for a build that antivirus kills right after it was started.
        service.StartExecutable = (_, _) => StartHidden("cmd.exe", "/d", "/c", "exit 3");
        service.ExitProcess = () => throw new InvalidOperationException("must not exit");

        bool ok = await service.ApplyUpdateAsync(UpdateFor(payload), TargetExe, restartAndExit: true, null, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(FakePe("old"), File.ReadAllBytes(TargetExe));
        Assert.Equal(new[] { TargetExe }, Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task ApplyUpdate_WhenNewExeVanishesAfterStart_RestoresThePreviousBuild()
    {
        File.WriteAllBytes(TargetExe, FakePe("old"));
        byte[] payload = FakePe("new");
        var service = ServiceServing(payload);
        service.StartExecutable = (path, _) =>
        {
            // Quarantined right after launch; the shell handed out no process handle.
            File.Delete(path);
            return null;
        };
        service.ExitProcess = () => throw new InvalidOperationException("must not exit");

        bool ok = await service.ApplyUpdateAsync(UpdateFor(payload), TargetExe, restartAndExit: true, null, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(FakePe("old"), File.ReadAllBytes(TargetExe));
        Assert.Equal(new[] { TargetExe }, Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task ApplyUpdate_WhenNewBuildIsStillRunningAtTheTimeout_AssumesItIsAliveAndExits()
    {
        File.WriteAllBytes(TargetExe, FakePe("old"));
        byte[] payload = FakePe("new");
        var service = ServiceServing(payload);
        service.StartupConfirmationTimeout = TimeSpan.FromSeconds(1);
        int pid = 0;
        bool exited = false;
        service.StartExecutable = (_, _) =>
        {
            // Alive but slow to report in, e.g. while being scanned.
            var p = StartHidden(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n", "30", "127.0.0.1");
            pid = p.Id;
            return p;
        };
        service.ExitProcess = () => exited = true;

        try
        {
            bool ok = await service.ApplyUpdateAsync(UpdateFor(payload), TargetExe, restartAndExit: true, null, CancellationToken.None);

            Assert.True(ok);
            Assert.True(exited);
            Assert.Equal(payload, File.ReadAllBytes(TargetExe));
        }
        finally
        {
            try { using var p = Process.GetProcessById(pid); p.Kill(); p.WaitForExit(2000); } catch { }
        }
    }

    [Fact]
    public async Task ApplyUpdate_WhenUserCancelsTheSecurityPrompt_DoesNotAskAgain()
    {
        File.WriteAllBytes(TargetExe, FakePe("old"));
        byte[] payload = FakePe("new");
        var service = ServiceServing(payload);
        int attempts = 0;
        service.StartExecutable = (_, _) => { attempts++; throw new System.ComponentModel.Win32Exception(1223); };
        service.ExitProcess = () => throw new InvalidOperationException("must not exit");

        bool ok = await service.ApplyUpdateAsync(UpdateFor(payload), TargetExe, restartAndExit: true, null, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(1, attempts);
        Assert.Equal(FakePe("old"), File.ReadAllBytes(TargetExe));
    }

    [Fact]
    public void SignalUpdateStarted_SignalsOnlyTheNamedProcessAndIgnoresGarbage()
    {
        // A pid that is not this process, so no update in another test can collide.
        int fakeOldPid = Environment.ProcessId + 1_000_000;
        using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateService.GetStartupSignalName(fakeOldPid));

        UpdateService.SignalUpdateStarted(new[] { "--updated-from=", "--updated-from=abc", "--updated-from=-5", "unrelated", "--updated-from=99999999999" });
        Assert.False(signal.WaitOne(0));

        UpdateService.SignalUpdateStarted(new[] { $"--updated-from={fakeOldPid}" });
        Assert.True(signal.WaitOne(0));
    }

    [Fact]
    public void SignalUpdateStarted_AlsoAnswersAnUpdaterFromBeforeTheRename()
    {
        // MCmodsLoader v1.3.0 installs this build and waits on its own event name.
        int fakeOldPid = Environment.ProcessId + 2_000_000;
        using var legacySignal = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateService.GetLegacyStartupSignalName(fakeOldPid));

        UpdateService.SignalUpdateStarted(new[] { $"--updated-from={fakeOldPid}" });

        Assert.True(legacySignal.WaitOne(0));
        Assert.StartsWith(@"Local\MCmodsLoader.", UpdateService.GetLegacyStartupSignalName(fakeOldPid));
    }

    private static Process StartHidden(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);
        return Process.Start(psi)!;
    }

    [Fact]
    public async Task ApplyUpdate_WhenRestartFails_RestoresThePreviousBuild()
    {
        File.WriteAllBytes(TargetExe, FakePe("old"));
        byte[] payload = FakePe("new");
        var service = ServiceServing(payload);
        int attempts = 0;
        bool exited = false;
        service.StartExecutable = (_, _) => { attempts++; throw new System.ComponentModel.Win32Exception(5); };
        service.ExitProcess = () => exited = true;

        bool ok = await service.ApplyUpdateAsync(UpdateFor(payload), TargetExe, restartAndExit: true, null, CancellationToken.None);

        Assert.False(ok);
        Assert.False(exited);
        Assert.True(attempts > 1, "The restart should have been retried");
        Assert.Equal(FakePe("old"), File.ReadAllBytes(TargetExe));
        Assert.Equal(new[] { TargetExe }, Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task ApplyUpdate_WhenNewBuildIsQuarantinedBeforeRestart_RestoresThePreviousBuild()
    {
        File.WriteAllBytes(TargetExe, FakePe("old"));
        byte[] payload = FakePe("new");
        var service = ServiceServing(payload);
        service.StartExecutable = (path, _) =>
        {
            // An antivirus removing the new exe between the swap and the restart.
            File.Delete(path);
            throw new System.ComponentModel.Win32Exception(2);
        };
        service.ExitProcess = () => throw new InvalidOperationException("must not exit");

        bool ok = await service.ApplyUpdateAsync(UpdateFor(payload), TargetExe, restartAndExit: true, null, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(FakePe("old"), File.ReadAllBytes(TargetExe));
        Assert.Equal(new[] { TargetExe }, Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task ApplyUpdate_WhenBackupCannotBeRestored_KeepsTheNewBuildAndReportsSuccess()
    {
        File.WriteAllBytes(TargetExe, FakePe("old"));
        byte[] payload = FakePe("new");
        var service = ServiceServing(payload);
        FileStream? backupLock = null;
        service.StartExecutable = (_, _) =>
        {
            // The previous build is pinned (still running elsewhere, being scanned), so
            // it cannot be moved back; the new build must not be thrown away for that.
            backupLock ??= new FileStream(BackupExe, FileMode.Open, FileAccess.Read, FileShare.Read);
            throw new System.ComponentModel.Win32Exception(5);
        };
        service.ExitProcess = () => throw new InvalidOperationException("must not exit");

        try
        {
            bool ok = await service.ApplyUpdateAsync(UpdateFor(payload), TargetExe, restartAndExit: true, null, CancellationToken.None);

            Assert.True(ok, "The new build is installed, it just was not started");
            Assert.Equal(payload, File.ReadAllBytes(TargetExe));
            Assert.True(File.Exists(BackupExe));
            Assert.False(File.Exists(StagedExe));
        }
        finally
        {
            backupLock?.Dispose();
        }
    }

    [Fact]
    public async Task CleanupAfterUpdate_DeletesAReadOnlyBackup()
    {
        File.WriteAllBytes(TargetExe, FakePe("old"));
        File.SetAttributes(TargetExe, FileAttributes.ReadOnly);
        byte[] payload = FakePe("new");

        bool ok = await ServiceServing(payload).ApplyUpdateAsync(UpdateFor(payload), TargetExe, restartAndExit: false, null, CancellationToken.None);
        Assert.True(ok);
        Assert.True(File.GetAttributes(BackupExe).HasFlag(FileAttributes.ReadOnly));

        await UpdateService.CleanupAfterUpdateAsync(TargetExe, TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { TargetExe }, Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task ApplyUpdate_WithDigestMismatch_IsRejectedAndLeavesNothingBehind()
    {
        File.WriteAllBytes(TargetExe, FakePe("old"));
        byte[] payload = FakePe("tampered");

        var update = UpdateFor(payload, sha256Override: new string('0', 64));
        bool ok = await ServiceServing(payload).ApplyUpdateAsync(update, TargetExe, restartAndExit: false, null, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(FakePe("old"), File.ReadAllBytes(TargetExe));
        Assert.Equal(new[] { TargetExe }, Directory.GetFiles(_tempDir));
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html>Rate limited</html>")]
    [InlineData("")]
    public async Task ApplyUpdate_WithNonPeDownload_IsRejected(string body)
    {
        File.WriteAllBytes(TargetExe, FakePe("old"));
        byte[] payload = Encoding.UTF8.GetBytes(body);

        // No digest published: only the PE header check stands between an error page
        // and the user's executable.
        var update = UpdateFor(null);
        bool ok = await ServiceServing(payload).ApplyUpdateAsync(update, TargetExe, restartAndExit: false, null, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(FakePe("old"), File.ReadAllBytes(TargetExe));
        Assert.Equal(new[] { TargetExe }, Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task CleanupAfterUpdate_RemovesLeftoversButNeverTheRunningExecutable()
    {
        File.WriteAllBytes(TargetExe, FakePe("current"));
        string uniqueBackup = Path.Combine(_tempDir, "MCperfBooster.old-0123abcd.exe");
        string legacyScript = Path.Combine(_tempDir, "update_restart.bat");
        string unrelated = Path.Combine(_tempDir, "notes.txt");
        foreach (string file in new[] { BackupExe, StagedExe, uniqueBackup, legacyScript, unrelated })
        {
            File.WriteAllText(file, "x");
        }

        await UpdateService.CleanupAfterUpdateAsync(TargetExe, TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { TargetExe, unrelated }.OrderBy(p => p), Directory.GetFiles(_tempDir).OrderBy(p => p));

        // Started from the staged file itself, cleanup must leave that file alone.
        File.WriteAllText(StagedExe, "x");
        await UpdateService.CleanupAfterUpdateAsync(StagedExe, TimeSpan.FromSeconds(2));
        Assert.True(File.Exists(StagedExe));

        // The public entry point is fire-and-forget and must not throw for odd paths.
        UpdateService.CleanupAfterUpdate(Path.Combine(_tempDir, "missing", "MCperfBooster.exe"));
    }

    [Fact]
    public async Task CleanupAfterUpdate_AfterTheUpdateFromBeforeTheRename_RemovesTheOldNamesLeftovers()
    {
        // MCmodsLoader v1.3.0 swaps this build in under its own file name and leaves its
        // backup and, after an aborted download, its staged file next to it.
        string runningExe = Path.Combine(_tempDir, "MCmodsLoader.exe");
        string oldBackup = Path.Combine(_tempDir, "MCmodsLoader.old.exe");
        string oldStaged = Path.Combine(_tempDir, "MCmodsLoader.new.exe");
        File.WriteAllBytes(runningExe, FakePe("current"));
        File.WriteAllText(oldBackup, "x");
        File.WriteAllText(oldStaged, "x");

        await UpdateService.CleanupAfterUpdateAsync(runningExe, TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { runningExe }, Directory.GetFiles(_tempDir));
    }

    [Theory]
    [InlineData("sha256:1F8A9418C4DF839DE5AD2358D6E9EB23C15DDDF81AA92B137DDE54365D331B5C", "1f8a9418c4df839de5ad2358d6e9eb23c15dddf81aa92b137dde54365d331b5c")]
    [InlineData("sha512:abcdef", null)]
    [InlineData("sha256:not-hex", null)]
    [InlineData(null, null)]
    public void ParseSha256Digest_AcceptsOnlyWellFormedSha256(string? digest, string? expected)
    {
        Assert.Equal(expected, UpdateService.ParseSha256Digest(digest));
    }
}

public class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_WithHundredsOfKilobytesOnStderr_CompletesWithoutDeadlock()
    {
        // A few large writes rather than thousands of echos, so CPU load cannot stretch
        // the run; the old sequential read blocked as soon as stderr filled the pipe.
        string line = new string('e', 100);
        string flood = Path.Combine(Path.GetTempPath(), $"stderr_flood_{Guid.NewGuid():N}.txt");
        File.WriteAllText(flood, string.Concat(Enumerable.Repeat(line + "\r\n", 4000)));
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                "cmd.exe",
                new[] { "/d", "/c", "type", flood, "1>&2" },
                TimeSpan.FromSeconds(120));
        }
        finally
        {
            File.Delete(flood);
        }

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.StandardError.Length > 300_000, $"Expected >300 KB on stderr, got {result.StandardError.Length} chars");
        Assert.Equal(line, ProcessRunner.Tail(result.StandardError, maxLines: 1));
    }

    [Fact]
    public async Task RunAsync_WhenProcessHangs_KillsTheWholeTreeAfterTheTimeout()
    {
        // The hanging grandchild runs under a unique image name so it can be looked up.
        string dir = Path.Combine(Path.GetTempPath(), $"RunnerTree_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string imageName = $"hang_{Guid.NewGuid():N}";
        string grandchild = Path.Combine(dir, imageName + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), grandchild);
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await ProcessRunner.RunAsync(
                "cmd.exe",
                new[] { "/d", "/c", grandchild, "-n", "30", "127.0.0.1" },
                TimeSpan.FromSeconds(1));
            sw.Stop();

            Assert.True(result.TimedOut);

            // The grandchild inherited the output pipes, so had it survived, draining
            // them would have run into the runner's 5 s grace period.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Took {sw.Elapsed}");
            Assert.Empty(Process.GetProcessesByName(imageName));
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName(imageName))
            {
                try { p.Kill(); p.WaitForExit(2000); } catch { }
            }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Tail_KeepsTheLastLinesOnOneLine()
    {
        Assert.Equal("b | c", ProcessRunner.Tail("a\r\nb\r\n\r\nc\r\n", maxLines: 2));
        Assert.Equal("", ProcessRunner.Tail("   "));
    }
}

public class FabricPlatformTests : IDisposable
{
    private readonly string _tempDir;

    public FabricPlatformTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FabricPlatformTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private string CreateFakeJava(params string[] relativeDir)
    {
        string dir = Path.Combine(new[] { _tempDir }.Concat(relativeDir).ToArray());
        Directory.CreateDirectory(dir);
        string java = Path.Combine(dir, "java.exe");
        File.WriteAllText(java, "not really java");
        return java;
    }

    [Fact]
    public void FindJavaExecutable_IgnoresRelativePathEntries()
    {
        string absoluteJava = CreateFakeJava("abs", "bin");

        // A java.exe below the working directory, which is exactly what must not be
        // searched. Created there rather than in %TEMP%, which can sit on another drive
        // (CI runners check out to D:), where no relative path reaches it.
        string relativeDir = Path.Combine($"RelJava_{Guid.NewGuid():N}", "bin");
        string plantedDir = Path.Combine(Environment.CurrentDirectory, relativeDir);
        Directory.CreateDirectory(plantedDir);
        try
        {
            File.WriteAllText(Path.Combine(plantedDir, "java.exe"), "planted");
            Assert.True(File.Exists(Path.Combine(relativeDir, "java.exe")));

            Assert.Null(FabricService.FindJavaExecutable(null, $".;{relativeDir};bin", Array.Empty<string>()));
            Assert.Null(FabricService.FindJavaExecutable(Path.GetDirectoryName(relativeDir), null, Array.Empty<string>()));

            string path = $"{relativeDir};\"{Path.GetDirectoryName(absoluteJava)}\"";
            Assert.Equal(absoluteJava, FabricService.FindJavaExecutable(null, path, Array.Empty<string>()));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(plantedDir)!, true); } catch { }
        }
    }

    [Fact]
    public void FindJavaExecutable_PrefersJavaHomeThenPathThenLauncherRuntime()
    {
        string homeJava = CreateFakeJava("home", "bin");
        string pathJava = CreateFakeJava("onpath");
        string runtimeJava = CreateFakeJava("runtime", "java-runtime-delta", "windows-x64", "java-runtime-delta", "bin");
        string runtimeRoot = Path.Combine(_tempDir, "runtime");
        string javaHome = Path.GetDirectoryName(Path.GetDirectoryName(homeJava))!;
        string pathVar = Path.GetDirectoryName(pathJava)!;

        Assert.Equal(homeJava, FabricService.FindJavaExecutable(javaHome, pathVar, new[] { runtimeRoot }));
        Assert.Equal(pathJava, FabricService.FindJavaExecutable(null, pathVar, new[] { runtimeRoot }));
        Assert.Equal(runtimeJava, FabricService.FindJavaExecutable(null, null, new[] { runtimeRoot }));
        Assert.Null(FabricService.FindJavaExecutable(null, null, new[] { Path.Combine(_tempDir, "nothing-here") }));
    }

    [Fact]
    public void FindJavaExecutable_SearchesUnknownRuntimeLayouts()
    {
        string java = CreateFakeJava("runtime", "java-runtime-epsilon", "windows-x64", "java-runtime-epsilon", "bin");
        Assert.Equal(java, FabricService.FindJavaExecutable(null, null, new[] { Path.Combine(_tempDir, "runtime") }));
    }

    [Theory]
    [InlineData(@"[
        { ""loader"": { ""version"": ""0.20.0-beta.2"", ""stable"": false } },
        { ""loader"": { ""version"": ""0.19.5"", ""stable"": true } },
        { ""loader"": { ""version"": ""0.19.4"", ""stable"": true } }
    ]", "0.19.5")]
    [InlineData(@"[
        { ""loader"": { ""version"": ""0.20.0-beta.2"", ""stable"": false } },
        { ""loader"": { ""version"": ""0.20.0-beta.1"", ""stable"": false } }
    ]", "0.20.0-beta.2")]
    [InlineData("[]", null)]
    public async Task GetLatestLoaderVersionAsync_PrefersTheNewestStableBuild(string metaResponse, string? expected)
    {
        using var client = new HttpClient(new MockHttpHandler(metaResponse));
        var service = new FabricService(client);

        Assert.Equal(expected, await service.GetLatestLoaderVersionAsync("26.2"));
    }

    private void CreateVersion(string versionId, bool withJson = true)
    {
        string dir = Path.Combine(_tempDir, "versions", versionId);
        Directory.CreateDirectory(dir);
        if (withJson)
            File.WriteAllText(Path.Combine(dir, $"{versionId}.json"), "{}");
    }

    [Fact]
    public async Task CheckFabricStatusAsync_WithoutVersionJson_ReportsNotInstalled()
    {
        // The folder survives an uninstall while its json is gone, and the launcher
        // profile still points at it. Neither makes Fabric usable.
        CreateVersion("fabric-loader-0.19.5-26.2", withJson: false);
        File.WriteAllText(Path.Combine(_tempDir, "launcher_profiles.json"), @"{
            ""profiles"": { ""fabric-loader-26.2"": { ""name"": ""Fabric"", ""lastVersionId"": ""fabric-loader-0.19.5-26.2"" } }
        }");

        var status = await new FabricService().CheckFabricStatusAsync(_tempDir, "26.2");

        Assert.False(status.IsInstalled);
        Assert.Null(status.InstalledLoaderVersion);
        Assert.Null(status.InstalledVersionId);
        Assert.False(status.HasLauncherProfile);
        Assert.Null(status.ProfileName);
    }

    [Theory]
    [InlineData(null)]
    // A profile on a loader build that is no longer installed cannot be launched.
    [InlineData(@"{ ""profiles"": { ""stale"": { ""name"": ""Stale"", ""lastVersionId"": ""fabric-loader-0.15.0-26.2"" } } }")]
    public async Task CheckFabricStatusAsync_WithoutUsableProfile_ReportsInstalledWithoutProfileName(string? profilesJson)
    {
        CreateVersion("fabric-loader-0.19.5-26.2");
        if (profilesJson != null)
            File.WriteAllText(Path.Combine(_tempDir, "launcher_profiles.json"), profilesJson);

        var status = await new FabricService().CheckFabricStatusAsync(_tempDir, "26.2");

        Assert.True(status.IsInstalled);
        Assert.Equal("0.19.5", status.InstalledLoaderVersion);
        Assert.Equal("fabric-loader-0.19.5-26.2", status.InstalledVersionId);
        Assert.False(status.HasLauncherProfile);
        Assert.Null(status.ProfileName);
    }

    [Fact]
    public async Task EnsureLauncherProfileAsync_WhenProfileIsMissing_RegistersOne()
    {
        CreateVersion("fabric-loader-0.18.3-26.2");
        CreateVersion("fabric-loader-0.19.5-26.2");

        var status = await new FabricService().EnsureLauncherProfileAsync(_tempDir, "26.2");

        Assert.True(status.HasLauncherProfile);
        Assert.Equal("Fabric 26.2", status.ProfileName);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, "launcher_profiles.json")));
        Assert.Equal("fabric-loader-0.19.5-26.2",
            doc.RootElement.GetProperty("profiles").GetProperty("fabric-loader-26.2").GetProperty("lastVersionId").GetString());
    }

    [Fact]
    public async Task EnsureLauncherProfileAsync_RepointsAStaleProfileAndKeepsItsName()
    {
        CreateVersion("fabric-loader-0.19.5-26.2");
        File.WriteAllText(Path.Combine(_tempDir, "launcher_profiles.json"),
            @"{ ""profiles"": { ""mine"": { ""name"": ""My Fabric"", ""lastVersionId"": ""fabric-loader-0.15.0-26.2"", ""javaArgs"": ""-Xmx8G"" } } }");

        var status = await new FabricService().EnsureLauncherProfileAsync(_tempDir, "26.2");

        Assert.True(status.HasLauncherProfile);
        Assert.Equal("My Fabric", status.ProfileName);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, "launcher_profiles.json")));
        var profiles = doc.RootElement.GetProperty("profiles");
        Assert.Single(profiles.EnumerateObject());
        Assert.Equal("-Xmx8G", profiles.GetProperty("mine").GetProperty("javaArgs").GetString());
    }

    [Fact]
    public async Task EnsureLauncherProfileAsync_LeavesExistingProfilesAndMissingFabricAlone()
    {
        var service = new FabricService();

        // Not installed: nothing to register, and no profiles file gets created.
        var notInstalled = await service.EnsureLauncherProfileAsync(_tempDir, "26.2");
        Assert.False(notInstalled.IsInstalled);
        Assert.False(File.Exists(Path.Combine(_tempDir, "launcher_profiles.json")));

        // Installed with a working profile: the file is not rewritten.
        CreateVersion("fabric-loader-0.19.5-26.2");
        string profilesFile = Path.Combine(_tempDir, "launcher_profiles.json");
        string json = @"{ ""profiles"": { ""f"": { ""name"": ""Fabric"", ""lastVersionId"": ""fabric-loader-0.19.5-26.2"" } } }";
        File.WriteAllText(profilesFile, json);

        var status = await service.EnsureLauncherProfileAsync(_tempDir, "26.2");

        Assert.Equal("Fabric", status.ProfileName);
        Assert.Equal(json, File.ReadAllText(profilesFile));

        // A path that cannot even be listed yields a status rather than an exception.
        var bogus = await service.EnsureLauncherProfileAsync("\0invalid", "26.2");
        Assert.False(bogus.IsInstalled);
    }

    [Fact]
    public async Task CheckFabricStatusAsync_WithSeveralProfiles_PrefersNewestLoaderThenMostRecentlyUsed()
    {
        CreateVersion("fabric-loader-0.18.3-26.2");
        CreateVersion("fabric-loader-0.19.5-26.2");
        File.WriteAllText(Path.Combine(_tempDir, "launcher_profiles.json"), @"{
            // Comments and trailing commas are written by hand-edited launchers too.
            ""profiles"": {
                ""old-but-recent"": { ""name"": ""Old loader"", ""lastVersionId"": ""fabric-loader-0.18.3-26.2"", ""lastUsed"": ""2026-10-01T10:00:00.000Z"" },
                ""missing"": { ""name"": ""Missing loader"", ""lastVersionId"": ""fabric-loader-0.20.0-26.2"", ""lastUsed"": ""2026-10-09T10:00:00.000Z"" },
                ""newest-stale"": { ""name"": ""Newest, stale"", ""lastVersionId"": ""fabric-loader-0.19.5-26.2"", ""lastUsed"": ""2025-01-01T10:00:00.000Z"" },
                ""newest-recent"": { ""name"": ""Newest, recent"", ""lastVersionId"": ""fabric-loader-0.19.5-26.2"", ""lastUsed"": ""2026-09-01T10:00:00.000Z"" },
            },
        }");

        var status = await new FabricService().CheckFabricStatusAsync(_tempDir, "26.2");

        Assert.True(status.IsInstalled);
        Assert.Equal("0.19.5", status.InstalledLoaderVersion);
        Assert.Equal("Newest, recent", status.ProfileName);
    }

    private JsonElement RegisterAndReadProfiles(string profilesJson)
    {
        string profilesFile = Path.Combine(_tempDir, "launcher_profiles.json");
        File.WriteAllText(profilesFile, profilesJson);
        FabricService.RegisterProfileInLauncher(_tempDir, "26.2", "fabric-loader-0.19.5-26.2");
        using var doc = JsonDocument.Parse(File.ReadAllText(profilesFile));
        return doc.RootElement.GetProperty("profiles").Clone();
    }

    private static string LastVersion(JsonElement profiles, string key) =>
        profiles.GetProperty(key).GetProperty("lastVersionId").GetString()!;

    [Fact]
    public void RegisterProfileInLauncher_PrefersOwnFabricProfileOverTheUsersOwn()
    {
        var profiles = RegisterAndReadProfiles(@"{ ""profiles"": {
            ""users-own"": { ""name"": ""Mine"", ""lastVersionId"": ""fabric-loader-0.15.0-26.2"", ""lastUsed"": ""2026-10-10T10:00:00.000Z"" },
            ""fabric-loader-26.2"": { ""name"": ""Fabric"", ""lastVersionId"": ""fabric-loader-0.18.3-26.2"", ""lastUsed"": ""2026-01-01T10:00:00.000Z"" }
        } }");

        Assert.Equal("fabric-loader-0.19.5-26.2", LastVersion(profiles, "fabric-loader-26.2"));
        Assert.Equal("fabric-loader-0.15.0-26.2", LastVersion(profiles, "users-own"));
        // Re-pointing is not using: lastUsed stays the launcher's.
        Assert.Equal("2026-01-01T10:00:00.000Z", profiles.GetProperty("fabric-loader-26.2").GetProperty("lastUsed").GetString());
    }

    [Fact]
    public void RegisterProfileInLauncher_WithoutOwnProfile_RepointsTheMostRecentlyUsedFabricProfile()
    {
        var profiles = RegisterAndReadProfiles(@"{ ""profiles"": {
            ""older"": { ""name"": ""Older"", ""lastVersionId"": ""fabric-loader-0.15.0-26.2"", ""lastUsed"": ""2025-05-01T10:00:00.000Z"" },
            ""other-version"": { ""name"": ""Other"", ""lastVersionId"": ""fabric-loader-0.15.0-1.21.4"", ""lastUsed"": ""2026-10-11T10:00:00.000Z"" },
            ""recent"": { ""name"": ""Recent"", ""lastVersionId"": ""fabric-loader-0.16.0-26.2"", ""lastUsed"": ""2026-09-01T10:00:00.000Z"" }
        } }");

        Assert.Equal("fabric-loader-0.19.5-26.2", LastVersion(profiles, "recent"));
        Assert.Equal("fabric-loader-0.15.0-26.2", LastVersion(profiles, "older"));
        Assert.Equal("fabric-loader-0.15.0-1.21.4", LastVersion(profiles, "other-version"));
        Assert.Equal("2026-09-01T10:00:00.000Z", profiles.GetProperty("recent").GetProperty("lastUsed").GetString());
        Assert.Equal(3, profiles.EnumerateObject().Count());
    }

    [Fact]
    public void RegisterProfileInLauncher_LeavesOwnProfileThatTheUserSwitchedAway()
    {
        // The user pointed our profile at Quilt; it is theirs now.
        var profiles = RegisterAndReadProfiles(@"{ ""profiles"": {
            ""fabric-loader-26.2"": { ""name"": ""Now Quilt"", ""lastVersionId"": ""quilt-loader-0.26.0-26.2"" }
        } }");

        Assert.Equal("quilt-loader-0.26.0-26.2", LastVersion(profiles, "fabric-loader-26.2"));
        Assert.Equal("fabric-loader-0.19.5-26.2", LastVersion(profiles, "fabric-loader-26.2-2"));
        Assert.Equal("Fabric 26.2", profiles.GetProperty("fabric-loader-26.2-2").GetProperty("name").GetString());
    }

    [Fact]
    public void RegisterProfileInLauncher_RewritesProfilesAtomicallyAndKeepsOtherProfiles()
    {
        string profilesFile = Path.Combine(_tempDir, "launcher_profiles.json");
        File.WriteAllText(profilesFile, @"{
            // written by hand
            ""profiles"": {
                ""vanilla"": { ""name"": ""Latest Release ä"", ""lastVersionId"": ""latest-release"" },
            },
            ""settings"": { ""keepLauncherOpen"": true },
        }");

        FabricService.RegisterProfileInLauncher(_tempDir, "26.2", "fabric-loader-0.19.5-26.2");

        using var doc = JsonDocument.Parse(File.ReadAllText(profilesFile));
        var profiles = doc.RootElement.GetProperty("profiles");
        Assert.Equal("Latest Release ä", profiles.GetProperty("vanilla").GetProperty("name").GetString());
        Assert.Equal("fabric-loader-0.19.5-26.2", profiles.GetProperty("fabric-loader-26.2").GetProperty("lastVersionId").GetString());
        Assert.True(doc.RootElement.GetProperty("settings").GetProperty("keepLauncherOpen").GetBoolean());
        Assert.Equal(new[] { profilesFile }, Directory.GetFiles(_tempDir));
    }
}

public class MinecraftDirectoryTests : IDisposable
{
    private readonly string _tempDir;

    public MinecraftDirectoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"McValidTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("MinecraftLauncher", true)]
    [InlineData("minecraftlauncher.exe", true)]
    [InlineData("Minecraft", true)]
    [InlineData("Minecraft.Windows", false)]
    [InlineData("javaw", false)]
    [InlineData("MinecraftLauncherHelper", false)]
    [InlineData("", false)]
    public void IsLauncherProcessName_MatchesOnlyTheJavaEditionLaunchers(string processName, bool expected)
    {
        Assert.Equal(expected, MinecraftService.IsLauncherProcessName(processName));
    }

    [Fact]
    public void IsLauncherRunning_DoesNotThrow()
    {
        // The result depends on the machine; enumerating every process must not fail.
        _ = MinecraftService.IsLauncherRunning();
    }

    [Fact]
    public void IsValidMinecraftDirectory_RequiresLauncherFilesOrVersionsFolder()
    {
        var service = new MinecraftService();

        Assert.False(service.IsValidMinecraftDirectory(""));
        Assert.False(service.IsValidMinecraftDirectory(Path.Combine(_tempDir, "missing")));

        // An ordinary folder such as Documents.
        File.WriteAllText(Path.Combine(_tempDir, "letter.txt"), "x");
        Assert.False(service.IsValidMinecraftDirectory(_tempDir));

        foreach (string marker in new[] { "launcher_profiles.json", "launcher_profiles_microsoft_store.json", "versions" })
        {
            string dir = Path.Combine(_tempDir, marker.Replace('.', '_'));
            Directory.CreateDirectory(dir);
            if (marker == "versions")
                Directory.CreateDirectory(Path.Combine(dir, marker));
            else
                File.WriteAllText(Path.Combine(dir, marker), "{}");

            Assert.True(service.IsValidMinecraftDirectory(dir), marker);
        }
    }

    [Fact]
    public void GetInstalledVersions_ReadsLauncherProfilesWithCommentsAndTrailingCommas()
    {
        File.WriteAllText(Path.Combine(_tempDir, "launcher_profiles.json"), @"{
            // hand-edited
            ""profiles"": {
                ""a"": { ""lastVersionId"": ""1.21.4"", },
                ""b"": { ""lastVersionId"": ""fabric-loader-0.19.5-26.2"" },
            },
        }");

        var versions = new MinecraftService().GetInstalledVersions(_tempDir);

        Assert.Equal(new[] { "26.2", "1.21.4" }, versions);
    }
}

public class PresetAndSettingsPersistenceTests : IDisposable
{
    private readonly string _tempDir;

    public PresetAndSettingsPersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PersistTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                foreach (var f in Directory.GetFiles(_tempDir))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(_tempDir, true);
            }
            catch { }
        }
    }

    private static string ModListJson(params string[] slugs) =>
        "[" + string.Join(",", slugs.Select(s => $"{{ \"slug\": \"{s}\", \"name\": \"{s}\", \"fabricModId\": \"{s}\" }}")) + "]";

    private static string? ReadCache()
    {
        string path = ModPresets.GetLocalCacheFilePath();
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact]
    public void LocalCacheFilePath_IsRedirectedAwayFromTheRealProfile()
    {
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCperfBooster");

        Assert.StartsWith(TestEnvironment.RunDirectory, ModPresets.GetLocalCacheFilePath());
        Assert.False(ModPresets.GetLocalCacheFilePath().StartsWith(real, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("sodium", "lithium", "fabric-api")]
    [InlineData("sodium", "lithium", "fabric-api", "polytone", "sodium", "modmenu")]
    public async Task TryUpdateFromRemoteAsync_RejectsBrokenListsWithoutCachingThem(params string[] slugs)
    {
        string? cacheBefore = ReadCache();
        using var client = new HttpClient(new MockHttpHandler(ModListJson(slugs)));

        bool updated = await ModPresets.TryUpdateFromRemoteAsync(client, "https://dummy.url/mods.json");

        Assert.False(updated);
        Assert.Equal(cacheBefore, ReadCache());
        // polytone is not part of the pack, so finding it would mean the broken list was adopted.
        Assert.DoesNotContain(ModPresets.GetDefaultFpsModPack(), m => m.Slug == "polytone");
    }

    [Fact]
    public async Task TryUpdateFromRemoteAsync_ReportsOnlyActualChangesAndSeedsAMissingCache()
    {
        // Based on the embedded list, and changing only a display name, so other tests
        // reading the shared pack concurrently see the same mods and count throughout.
        using var stream = typeof(ModPresets).Assembly.GetManifestResourceStream(
            typeof(ModPresets).Assembly.GetManifestResourceNames().Single(n => n.EndsWith("default_mods.v2.json")))!;
        string original = new StreamReader(stream).ReadToEnd();
        var mods = ModPresets.ParseJson(original);
        string renamedName = mods[^1].Name + " (renamed)";
        string renamed = "[" + string.Join(",", mods.Select((m, i) =>
            JsonSerializeEntry(m, i == mods.Count - 1 ? renamedName : m.Name))) + "]";

        try
        {
            File.Delete(ModPresets.GetLocalCacheFilePath());

            // Same entries as the list in use: no change reported, but the cache is seeded.
            Assert.False(await FetchAsync(original));
            Assert.Equal(original, ReadCache());

            Assert.True(await FetchAsync(renamed));
            Assert.Equal(renamedName, ModPresets.GetDefaultFpsModPack()[^1].Name);
            Assert.Equal(renamed, ReadCache());
            Assert.False(await FetchAsync(renamed));

            Assert.True(await FetchAsync(original));
            Assert.Equal(original, ReadCache());
        }
        finally
        {
            await FetchAsync(original);
        }

        Assert.Equal(ModPresets.LoadFromResource().Select(m => m.Name), ModPresets.GetDefaultFpsModPack().Select(m => m.Name));
    }

    private static async Task<bool> FetchAsync(string json)
    {
        using var client = new HttpClient(new MockHttpHandler(json));
        return await ModPresets.TryUpdateFromRemoteAsync(client, "https://dummy.url/mods.json");
    }

    // Keeps the optional-mod fields: tests reading the shared pack meanwhile must not see
    // the optional mods turn mandatory.
    private static string JsonSerializeEntry(ModDefinition mod, string name)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("slug", mod.Slug);
            writer.WriteString("name", name);
            writer.WriteString("fabricModId", mod.FabricModId);
            if (mod.IsOptional)
            {
                writer.WriteBoolean("optional", true);
                writer.WriteBoolean("enabledByDefault", mod.EnabledByDefault);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    [Fact]
    public void AppSettingsSave_ReplacesContentAtomically()
    {
        string path = Path.Combine(_tempDir, "settings.json");
        var first = new AppSettings();
        first.SetOptionalModEnabled("xaeros-minimap", false);
        first.Save(path);

        var second = new AppSettings();
        second.SetOptionalModEnabled("xaeros-minimap", true);
        second.SetOptionalModEnabled("xaeros-world-map", false);
        second.Save(path);

        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var choices = doc.RootElement.GetProperty("optionalMods");
            Assert.True(choices.GetProperty("xaeros-minimap").GetBoolean());
            Assert.False(choices.GetProperty("xaeros-world-map").GetBoolean());
        }
        Assert.Equal(new[] { path }, Directory.GetFiles(_tempDir));
    }

    [Fact]
    public void AppSettingsSave_WhenTheFileCannotBeReplaced_KeepsTheOldContent()
    {
        string path = Path.Combine(_tempDir, "settings.json");
        var settings = new AppSettings();
        settings.SetOptionalModEnabled("xaeros-minimap", false);
        settings.Save(path);
        string before = File.ReadAllText(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        settings.SetOptionalModEnabled("xaeros-minimap", true);
        settings.Save(path);

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_tempDir));
    }
}
