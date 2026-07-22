using System;
using System.IO;
using FluentAssertions;
using VideoSplitJoiner.Core.Errors;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Unit tests for <see cref="ErrorLogWriter"/> (T-037): the writer produces a file containing the
/// FULL stderr + the exact command + exit code, and a write-failure is swallowed (never throws).
/// All I/O is redirected to a temp directory so the tests never touch the user's app-data folder.
/// </summary>
public sealed class ErrorLogWriterTests : IDisposable
{
    private readonly string _dir;

    public ErrorLogWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsj-log-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void TryWrite_CreatesFile_WithFullStdErr_Command_AndExitCode()
    {
        var writer = new ErrorLogWriter(_dir);
        var fullStdErr = string.Join(Environment.NewLine,
            "line 1 of many",
            "line 2",
            "Conversion failed! this is the real cause");

        var path = writer.TryWrite("split", "ffmpeg -i in.mp4 -c copy out.mp4", -22, fullStdErr);

        path.Should().NotBeNull();
        File.Exists(path!).Should().BeTrue();
        path!.Should().StartWith(_dir);
        Path.GetFileName(path).Should().StartWith("split-").And.EndWith(".log");

        var content = File.ReadAllText(path!);
        content.Should().Contain("ffmpeg -i in.mp4 -c copy out.mp4", "the exact command is logged");
        content.Should().Contain("-22", "the exit code is logged");
        content.Should().Contain("line 1 of many").And.Contain("Conversion failed! this is the real cause");
        content.Should().Contain("Timestamp", "a timestamp is recorded");
    }

    [Fact]
    public void TryWrite_CreatesLogDirectoryOnDemand()
    {
        Directory.Exists(_dir).Should().BeFalse("precondition: the dir does not exist yet");

        var writer = new ErrorLogWriter(_dir);
        var path = writer.TryWrite("join", "ffmpeg -f concat -i list.txt out.mp4", 1, "some stderr");

        path.Should().NotBeNull();
        Directory.Exists(_dir).Should().BeTrue("the writer creates the log dir on demand");
    }

    [Fact]
    public void TryWrite_WriteFailure_IsSwallowed_ReturnsNull()
    {
        // Point the writer at a path that is actually a FILE, so CreateDirectory throws internally.
        var blocker = Path.Combine(Path.GetTempPath(), "vsj-log-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "I am a file, not a directory");

        try
        {
            var logDirUnderAFile = Path.Combine(blocker, "logs");
            var writer = new ErrorLogWriter(logDirUnderAFile);

            string? path = null;
            Action act = () => path = writer.TryWrite("split", "cmd", 1, "stderr");

            act.Should().NotThrow("a logging failure must never crash the op");
            path.Should().BeNull("a failed write returns null, not a path");
        }
        finally
        {
            try { File.Delete(blocker); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void BuildLogBody_IsDeterministicShape_NoFileIo()
    {
        var body = ErrorLogWriter.BuildLogBody("split", "ffmpeg -x", 7, "the full stderr\nwith two lines");

        body.Should().Contain("Exit code : 7");
        body.Should().Contain("Command   : ffmpeg -x");
        body.Should().Contain("ffmpeg stderr (full)");
        body.Should().Contain("the full stderr");
        body.Should().Contain("with two lines");
    }

    // ---- TryWriteCrash / BuildCrashBody (T-079 global crash safety net) ----

    [Fact]
    public void TryWriteCrash_WritesFile_WithType_Message_AndStack()
    {
        var writer = new ErrorLogWriter(_dir);
        Exception caught;
        try
        {
            throw new InvalidOperationException("boom on the UI thread");
        }
        catch (Exception ex)
        {
            caught = ex; // captured with a real stack trace.
        }

        var path = writer.TryWriteCrash("Dispatcher", caught);

        path.Should().NotBeNull();
        File.Exists(path!).Should().BeTrue();
        Path.GetFileName(path).Should().StartWith("crash-dispatcher-").And.EndWith(".log");

        var content = File.ReadAllText(path!);
        content.Should().Contain("Dispatcher", "the crash source is recorded");
        content.Should().Contain("System.InvalidOperationException", "the exception type is logged");
        content.Should().Contain("boom on the UI thread", "the exception message is logged");
        content.Should().Contain("Stack", "the stack trace section is present");
        content.Should().Contain("TryWriteCrash_WritesFile", "the captured stack frame is logged");
        content.Should().Contain("Timestamp", "a timestamp is recorded");
    }

    [Fact]
    public void BuildCrashBody_IncludesInnerExceptionChain()
    {
        var inner = new FormatException("the inner cause");
        var outer = new InvalidOperationException("the outer wrapper", inner);

        var body = ErrorLogWriter.BuildCrashBody("AppDomain", outer);

        body.Should().Contain("AppDomain");
        body.Should().Contain("System.InvalidOperationException").And.Contain("the outer wrapper");
        body.Should().Contain("inner exception").And.Contain("System.FormatException").And.Contain("the inner cause");
    }

    [Fact]
    public void BuildCrashBody_NullException_DoesNotThrow_AndNotes_NoException()
    {
        string body = string.Empty;
        Action act = () => body = ErrorLogWriter.BuildCrashBody("AppDomain", null);

        act.Should().NotThrow("a null AppDomain payload must be handled gracefully");
        body.Should().Contain("no Exception object");
    }

    [Fact]
    public void TryWriteCrash_UnwritableDir_IsSwallowed_ReturnsNull()
    {
        // Point the writer at a path that is actually a FILE, so CreateDirectory throws internally.
        var blocker = Path.Combine(Path.GetTempPath(), "vsj-crash-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "I am a file, not a directory");

        try
        {
            var logDirUnderAFile = Path.Combine(blocker, "logs");
            var writer = new ErrorLogWriter(logDirUnderAFile);

            string? path = null;
            Action act = () => path = writer.TryWriteCrash("Dispatcher", new InvalidOperationException("x"));

            act.Should().NotThrow("a crash-logging failure must never crash the crash handler");
            path.Should().BeNull("a failed write returns null, not a path");
        }
        finally
        {
            try { File.Delete(blocker); } catch { /* best-effort */ }
        }
    }
}
